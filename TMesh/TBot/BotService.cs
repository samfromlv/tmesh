using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
using TBot.Models.MeshMessages;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TBot
{
    public class BotService
    {

        const int TrimUserNamesToLength = 8;

        public BotService(
            TelegramBotClient botClient,
            IOptions<TBotOptions> options,
            RegistrationService registrationService,
            MeshtasticService meshtasticService,
            IMemoryCache memoryCache,
            ILogger<BotService> logger)
        {
            _botClient = botClient;
            _options = options.Value;
            _registrationService = registrationService;
            _meshtasticService = meshtasticService;
            _memoryCache = memoryCache;
            _logger = logger;
        }
        private readonly TelegramBotClient _botClient;
        private readonly TBotOptions _options;
        private readonly RegistrationService _registrationService;
        private readonly MeshtasticService _meshtasticService;
        private readonly ILogger<BotService> _logger;
        private readonly IMemoryCache _memoryCache;

        public async Task InstallWebhook()
        {
            await _botClient.SetWebhook(
                _options.TelegramUpdateWebhookUrl,
                allowedUpdates: [UpdateType.Message],
                secretToken: _options.TelegramWebhookSecret);

            await _botClient.SetMyCommands(new[]
            {
                new BotCommand
                {
                    Command = "add",
                    Description = "Register a Meshtastic device (e.g., /add !aabbcc11)"
                },
                new BotCommand
                {
                    Command = "remove",
                    Description = "Unregister a Meshtastic device (e.g., /remove !aabbcc11)"
                },
                new BotCommand
                {
                    Command = "status",
                    Description = "Show list of registered Meshtastic devices"
                }
            });
        }

        public async Task<WebhookInfo> CheckInstall()
        {
            return await _botClient.GetWebhookInfo();
        }

        public void StoreMessageStatus(long meshtasticMessageId, MeshtasticMessageStatus status)
        {
            var currentDelay = _meshtasticService.EstimateDelay(MessagePriority.Normal);
            var cacheKey = $"MeshtasticMessageStatus_{meshtasticMessageId}";
            _memoryCache.Set(cacheKey, status, currentDelay.Add(TimeSpan.FromMinutes(Math.Max(currentDelay.TotalMinutes * 0.3, 3))));
        }

        public MeshtasticMessageStatus GetMessageStatus(long meshtasticMessageId)
        {
            var cacheKey = $"MeshtasticMessageStatus_{meshtasticMessageId}";
            if (_memoryCache.TryGetValue(cacheKey, out MeshtasticMessageStatus status))
            {
                return status;
            }
            return null;
        }

        public void ClearMessageStatus(long meshtasticMessageId)
        {
            var cacheKey = $"MeshtasticMessageStatus_{meshtasticMessageId}";
            _memoryCache.Remove(cacheKey);
        }

        private async Task HandleUpdate(Update update)
        {
            if (update.Type != UpdateType.Message
                || update.Message == null
                || update.Message.Chat == null
                || update.Message.From == null) return;

            var msg = update.Message;

            if (msg.From.IsBot && msg.From.Username == _options.TelegramBotUserName)
            {
                // Ignore messages from the bot itself
                return;
            }

            var chatId = msg.Chat.Id;
            var userId = msg.From.Id;
            var userName = msg.From.Username ?? $"{msg.From.FirstName} {msg.From.LastName}".Trim();


            var chatState = _registrationService.GetChatState(userId, chatId);

            switch (chatState)
            {
                case ChatState.Adding_NeedDeviceId:
                case ChatState.Adding_NeedCode:
                    await HandleDeviceAdd(userId, userName, chatId, msg, chatState.Value);
                    break;
                case ChatState.RemovingDevice:
                    {
                        await HandleDeviceRemove(userId, chatId, msg);
                        break;
                    }
                default:
                    {
                        await HandleDefaultUpdate(userId, userName, chatId, msg);
                        break;
                    }
            }
        }

        private async Task HandleDeviceAdd(
            long userId,
            string userName,
            long chatId,
            Message message,
            ChatState chatState)
        {
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await _botClient.SendMessage(chatId, "Registration canceled.");
                _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            if (chatState == ChatState.Adding_NeedDeviceId)
            {
                await ProcessNeedDeviceId(userId, chatId, message);
            }
            else if (chatState == ChatState.Adding_NeedCode)
            {
                await ProcessNeedCode(userId, userName, chatId, message);
            }
            else
            {
                _logger.LogWarning("Unexpected chat state {ChatState} in HandleDeviceAdd", chatState);
            }
        }

        private async Task HandleDeviceRemove(long userId, long chatId, Message message)
        {
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await _botClient.SendMessage(chatId, "Removal canceled.");
                _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await _botClient.SendMessage(chatId, "Please send a Meshtastic device ID to remove or /stop to cancel.");
                return;
            }
            if (!_meshtasticService.TryParseDeviceId(text, out var deviceId))
            {
                await _botClient.SendMessage(chatId, "Invalid device ID format. The device ID can be decimal or hex (hex starts with ! or #). Send /stop to cancel.");
                return;
            }

            var removed = await _registrationService.RemoveDeviceFromChatAsync(chatId, deviceId);
            if (!removed)
            {
                await _botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} is not registered in this chat.");
            }
            else
            {
                await _botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} removed from this chat.");
            }
            _registrationService.SetChatState(userId, chatId, ChatState.Default);
        }

        private async Task SetQueuedStatus(List<QueueResult> queueResults)
        {
            var first = queueResults[0];
            var status = GetMessageStatus(first.MessageId);
            if (status == null)
            {
                return;
            }

            lock (status)
            {
                var longestQueueDelay = queueResults.Max(qr => qr.EstimatedSendDelay);
                status.EstimatedSendDate = DateTime.UtcNow.Add(longestQueueDelay);
                foreach (var qr in queueResults)
                {
                    if (status.MeshMessages[qr.MessageId].Status == DeliveryStatus.Created)
                    {
                        status.MeshMessages[qr.MessageId].Status = DeliveryStatus.Queued;
                    }
                }
            }
            if (status.MeshMessages.Any(x => x.Value.Status != DeliveryStatus.Queued)
                || status.EstimatedSendDate.Value.Subtract(DateTime.UtcNow).TotalSeconds < 3)
            {
                return;
            }

            await ReportStatus(status);
        }

        public async Task UpdateMeshMessageStatus(
            long meshMessageId,
            DeliveryStatus newStatus,
            DeliveryStatus? maxCurrentStatus = null)
        {
            var status = GetMessageStatus(meshMessageId);
            if (status == null)
            {
                return;
            }
            lock (status)
            {
                if (status.MeshMessages.TryGetValue(meshMessageId, out var sts))
                {
                    if (maxCurrentStatus.HasValue
                        && sts.Status > maxCurrentStatus.Value)
                    {
                        return;
                    }

                    sts.Status = newStatus;
                }
            }
            await ReportStatus(status);
        }

        public async Task SetAckMeshMessageStatus(long meshMessageId, long fromDeviceId)
        {
            var status = GetMessageStatus(meshMessageId);
            if (status == null)
            {
                return;
            }
            lock (status)
            {
                if (status.MeshMessages.TryGetValue(meshMessageId, out var sts))
                {
                    var newStatus = fromDeviceId == sts.DeviceId
                        ? DeliveryStatus.Delivered
                        : DeliveryStatus.Acknowledged;

                    sts.Status = newStatus;
                }
            }
            await ReportStatus(status);
        }




        private async Task ReportStatus(MeshtasticMessageStatus status)
        {
            if (status.MeshMessages.All(x => x.Value.Status == DeliveryStatus.Delivered))
            {
                var deliveryStatus = status.MeshMessages.First().Value;
                string reactionEmoji = ConvertDeliveryStatusToString(deliveryStatus.Status);

                await _botClient.SetMessageReaction(
                          status.TelegramChatId,
                          status.TelegramMessageId,
                          [reactionEmoji]);

                int? deletedReplyId = null;
                if (status.BotReplyId != null)
                {
                    if (deliveryStatus.Status != DeliveryStatus.Queued)
                    {
                        deletedReplyId = status.BotReplyId;
                        status.BotReplyId = null;
                    }
                }
                if (deletedReplyId != null)
                {
                    await _botClient.DeleteMessage(
                        status.TelegramChatId,
                        deletedReplyId.Value);
                }
            }
            else
            {
                var sb = new StringBuilder("Status: ");
                var statusesOrdered = status.MeshMessages.OrderBy(x => x.Key).ToList();
                foreach (var (messageId, deliveryStatus) in statusesOrdered)
                {
                    sb.Append(ConvertDeliveryStatusToString(deliveryStatus.Status));
                }
                if (statusesOrdered.Any(x => x.Value.Status == DeliveryStatus.Queued)
                    && status.EstimatedSendDate.HasValue)
                {
                    var waitTimeSeconds = Math.Ceiling((status.EstimatedSendDate.Value - DateTime.UtcNow).TotalSeconds);
                    if (waitTimeSeconds >= 2)
                    {
                        sb.Append($". Queue wait: {waitTimeSeconds} seconds");
                    }
                }

                if (status.BotReplyId != null)
                {
                    var replyMsg = await _botClient.EditMessageText(
                        status.TelegramChatId,
                        status.BotReplyId.Value, sb.ToString());

                    status.BotReplyId = replyMsg.MessageId;
                }
                else
                {
                    var replyMsg = await _botClient.SendMessage(
                           status.TelegramChatId,
                           sb.ToString(),
                           replyParameters: new ReplyParameters
                           {
                               AllowSendingWithoutReply = false,
                               ChatId = status.TelegramChatId,
                               MessageId = status.TelegramMessageId,
                           });

                    status.BotReplyId = replyMsg.MessageId;
                }
            }
        }

        private string ConvertDeliveryStatusToString(DeliveryStatus status)
        {
            return status switch
            {
                DeliveryStatus.Created => ReactionEmoji.WritingHand,
                DeliveryStatus.Queued => ReactionEmoji.Eyes,
                DeliveryStatus.SentToMqtt => ReactionEmoji.Dove,
                DeliveryStatus.Acknowledged => ReactionEmoji.ManShrugging,
                DeliveryStatus.Delivered => ReactionEmoji.OkHand,
                DeliveryStatus.Failed => ReactionEmoji.ThumbsDown,
                _ => ReactionEmoji.ExplodingHead,
            };
        }

        private async Task ProcessNeedDeviceId(long userId, long chatId, Message message)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && _meshtasticService.TryParseDeviceId(message.Text, out var deviceId))
            {
                var userName = message.From?.Username ?? $"{message.From?.FirstName} {message.From?.LastName}".Trim();
                await ProcessDeviceIdForAdd(userId, userName, chatId, message.MessageId, deviceId);
            }
            else
            {
                await _botClient.SendMessage(chatId, "Invalid device ID. Please send a valid Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #). Send /stop to cancel.");
            }
        }

        private async Task ProcessDeviceIdForAdd(long userId, string userName, long chatId, int messageId, long deviceId)
        {
            var device = await _registrationService.GetDeviceAsync(deviceId);
            if (device == null)
            {
                await _botClient.SendMessage(chatId,
                    $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} has not yet been seen by the MQTT node {_options.MeshtasticNodeNameLong} in the Meshtastic network.\r\n" +
                    $"1. Ensure your primary channel is '{_options.MeshtasticPrimaryChannelName}' and the key is '{_options.MeshtasticPrimaryChannelPskBase64}'.\r\n" +
                    "2. Make sure 'OK to MQTT' is enabled in LoRa settings on your device.\r\n" +
                    $"3. Find node {_options.MeshtasticNodeNameLong} (MQTT) in node list, open it and click on 'Exchange user information'. {_options.MeshtasticNodeNameLong} broadcasts it's node info every {_options.SentTBotNodeInfoEverySeconds} seconds.\r\n\r\n" +
                    "Registration aborted.");
                _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            if (await _registrationService.HasRegistrationAsync(chatId, deviceId))
            {
                await _botClient.SendMessage(chatId, $"Device {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(deviceId)}) is already registered in this chat. Registration aborted.");
                _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            var codesSent = _registrationService.IncrementDeviceCodesSentRecently(deviceId);
            if (codesSent > RegistrationService.MaxCodeVerificationTries)
            {
                await _botClient.SendMessage(chatId, $"Device {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(deviceId)}) has reached the maximum number of verification codes sent. Please wait at least 1 hour before trying again to add the same device to any chats. Registration aborted.");
                _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            var code = _registrationService.GenerateRandomCode();
            _registrationService.StorePendingCodeAsync(userId, chatId, deviceId, code, DateTimeOffset.UtcNow.AddMinutes(5));

            var msg = await _botClient.SendMessage(chatId,
                $"Verification code sent to device {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(deviceId)}). Please reply with the received code here. The code is valid for 5 minutes.");

            await SendAndTrackMeshtasticMessage(
                device,
                chatId,
                msg.MessageId,
                $"TMesh verification code is: {code}");
            _registrationService.SetChatState(userId, chatId, Models.ChatState.Adding_NeedCode);
        }

        private string ExtractDeviceIdFromCommand(string commandText, string command)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return null;
            }

            // Remove the command part and trim
            var text = commandText.Substring(command.Length).Trim();

            // Return null if empty (no device ID provided)
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // Extract first word (device ID should not have spaces)
            var parts = text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : null;
        }

        private async Task SendAndTrackMeshtasticMessage(
            Device device,
            long chatId,
            int messageId,
            string text)
        {
            var newMeshMessageId = _meshtasticService.GetNextMeshtasticMessageId();
            StoreMessageStatus(newMeshMessageId, new MeshtasticMessageStatus
            {
                TelegramChatId = chatId,
                TelegramMessageId = messageId,
                MeshMessages = new Dictionary<long, DeliveryStatusWithDeviceId>
                    { {newMeshMessageId, new DeliveryStatusWithDeviceId
                    {
                        DeviceId = device.DeviceId,
                        Status = DeliveryStatus.Created,
                    } } },
                BotReplyId = null,
            });

            var queueResult = _meshtasticService.SendTextMessage(
                    newMeshMessageId,
                    device.DeviceId,
                    device.PublicKey,
                    text);

            await SetQueuedStatus([queueResult]);
        }

        private async Task SendAndTrackMeshtasticMessages(
            List<DeviceWithNameAndKey> devices,
            long chatId,
            int messageId,
            string text)
        {
            var status = new MeshtasticMessageStatus
            {
                TelegramChatId = chatId,
                TelegramMessageId = messageId,
                MeshMessages = new Dictionary<long, DeliveryStatusWithDeviceId>(devices.Count)
            };

            var deviceMessageIds = new List<(long deviceId, long messageId, byte[] publicKey)>();
            foreach (var device in devices)
            {
                var newMeshMessageId = _meshtasticService.GetNextMeshtasticMessageId();
                status.MeshMessages.Add(newMeshMessageId, new DeliveryStatusWithDeviceId
                {
                    DeviceId = device.DeviceId,
                    Status = DeliveryStatus.Created
                });
                StoreMessageStatus(newMeshMessageId, status);
                deviceMessageIds.Add((device.DeviceId, newMeshMessageId, device.PublicKey));
            }
            var queueResults = new List<QueueResult>();

            foreach (var (deviceId, newMeshMessageId, publicKey) in deviceMessageIds)
            {
                queueResults.Add(
                    _meshtasticService.SendTextMessage(
                        newMeshMessageId,
                        deviceId,
                        publicKey,
                        text));
            }
            await SetQueuedStatus(queueResults);
        }

        private async Task ProcessNeedCode(long userId, string userName, long chatId, Message message)
        {
            var maybeCode = message.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(maybeCode)
                  && _registrationService.IsValidCodeFormat(maybeCode))
            {
                if (await _registrationService.TryCreateRegistrationWithCode(
                    userId,
                    userName,
                    chatId,
                    maybeCode))
                {
                    await _botClient.SendMessage(chatId, "Registration successful.");
                    _registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                    return;
                }
                else
                {
                    await _botClient.SendMessage(chatId, "Invalid or expired code. Please check it and try again, or cancel with /stop.");
                    return;
                }
            }
            else
            {
                await _botClient.SendMessage(chatId, "Invalid code format. Please send the 6-digit verification code sent to your Meshtastic device. Send /stop to cancel.");
            }
        }

        private async Task HandleDefaultUpdate(
            long userId,
            string userName,
            long chatId,
            Message message)
        {
            if (message.Text?.StartsWith("/add", StringComparison.OrdinalIgnoreCase) == true)
            {
                var deviceIdFromCommand = ExtractDeviceIdFromCommand(message.Text, "/add");
                await StartAdd(userId, userName, chatId, message.MessageId, deviceIdFromCommand);
                return;
            }
            if (message.Text?.StartsWith("/remove", StringComparison.OrdinalIgnoreCase) == true)
            {
                var deviceIdFromCommand = ExtractDeviceIdFromCommand(message.Text, "/remove");
                await StartRemove(userId, chatId, message.MessageId, deviceIdFromCommand);
                return;
            }
            if (message.Text?.StartsWith("/status", StringComparison.OrdinalIgnoreCase) == true)
            {
                await HandleStatus(chatId);
                return;
            }

            await HandleText(
                message.MessageId,
                userId,
                userName,
                chatId,
                message.Text ?? string.Empty);
        }

        private async Task HandleText(
            int msgId,
            long userId,
            string userName,
            long chatId,
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var textToMesh = $"{StringHelper.Truncate(userName, TrimUserNamesToLength)}: {text}";

            if (!_meshtasticService.CanSendMessage(text))
            {
                await _botClient.SendMessage(
                    chatId,
                    $"Message is too long to send to a Meshtastic device. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes (English letters: 1 byte, Cyrillic: 2 bytes, emoji: 4 bytes).",
                    replyParameters: new ReplyParameters
                    {
                        AllowSendingWithoutReply = false,
                        ChatId = chatId,
                        MessageId = msgId,
                    });
                return;
            }

            var registrations = await _registrationService.GetDevicesByChatId(chatId);
            if (registrations.Count == 0)
            {
                await _botClient.SendMessage(
                    chatId,
                    "No registered devices. You can register a new device with the /add command. Please remove the bot from the group if you don't need it.",
                    replyParameters: new ReplyParameters
                    {
                        AllowSendingWithoutReply = false,
                        ChatId = chatId,
                        MessageId = msgId,
                    });
                return;
            }

            await SendAndTrackMeshtasticMessages(
                registrations,
                chatId,
                msgId,
                textToMesh);
        }

        private async Task HandleStatus(long chatId)
        {
            var devices = await _registrationService.GetDevicesByChatId(chatId);
            if (devices.Count == 0)
            {
                await _botClient.SendMessage(chatId, "No registered devices. You can register a new device with the /add command.");
            }
            else
            {
                var lines = devices.Select(d => $"• Device: {d.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId)})");
                var text = "Registered devices:\r\n" + string.Join("\r\n", lines);
                await _botClient.SendMessage(chatId, text);
            }
        }

        private async Task StartAdd(long userId, string userName, long chatId, int messageId, string deviceIdText)
        {
            if (string.IsNullOrWhiteSpace(deviceIdText))
            {
                // No device ID provided, ask for it
                await _botClient.SendMessage(chatId, "Please send your Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #).");
                _registrationService.SetChatState(userId, chatId, Models.ChatState.Adding_NeedDeviceId);
                return;
            }

            // Device ID provided in command, process immediately
            if (!_meshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await _botClient.SendMessage(chatId,
                    $"Invalid device ID format: '{deviceIdText}'. The device ID can be decimal or hex (hex starts with ! or #).\n\n" +
                    "Examples:\n" +
                    "• /add 123456789\n" +
                    "• /add !75bcd15\n" +
                    "• /add #75bcd15\n\n" +
                    "Or use /add without parameters and I'll ask for the device ID.");
                return;
            }

            // Process the device ID (same logic as ProcessNeedDeviceId)
            await ProcessDeviceIdForAdd(userId, userName, chatId, messageId, deviceId);
        }

        private async Task StartRemove(long userId, long chatId, int messageId, string deviceIdText)
        {
            if (string.IsNullOrWhiteSpace(deviceIdText))
            {
                // No device ID provided, ask for it
                await _botClient.SendMessage(chatId, "Please send your Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #).");
                _registrationService.SetChatState(userId, chatId, Models.ChatState.RemovingDevice);
                return;
            }

            // Device ID provided in command, process immediately
            if (!_meshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await _botClient.SendMessage(chatId,
                    $"Invalid device ID format: '{deviceIdText}'. The device ID can be decimal or hex (hex starts with ! or #).\n\n" +
                    "Examples:\n" +
                    "• /remove 123456789\n" +
                    "• /remove !75bcd15\n" +
                    "• /remove #75bcd15\n\n" +
                    "Or use /remove without parameters and I'll ask for the device ID.");
                return;
            }

            // Process removal immediately
            var removed = await _registrationService.RemoveDeviceFromChatAsync(chatId, deviceId);
            if (!removed)
            {
                await _botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} is not registered in this chat.");
            }
            else
            {
                await _botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} has been removed from this chat.");
            }
        }

        public static void Register(IServiceCollection services)
        {
            services.AddScoped(s =>
            {
                var options = s.GetRequiredService<IOptions<TBotOptions>>();
                return new TelegramBotClient(options.Value.TelegramApiToken);
            });
            services.AddSingleton<MeshtasticService>();
            services.AddScoped<RegistrationService>();
            services.AddScoped<BotService>();
        }

        public Task ProcessInboundTelegramMessage(string payload)
        {
            var update = JsonSerializer.Deserialize<Update>(payload, JsonBotAPI.Options);
            _logger.LogDebug("Processing inbound Telegram message: {Payload}", payload);
            return HandleUpdate(update);
        }

        private async Task<string> FormatTraceRouteMessage(TraceRouteMessage msg)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < msg.RouteDiscovery.Route.Count; i++)
            {
                var nodeId = msg.RouteDiscovery.Route[i];
                var snr = msg.RouteDiscovery.SnrTowards.Count > i
                    ? msg.RouteDiscovery.SnrTowards[i]
                    : sbyte.MinValue;

                string deviceName;
                if (_meshtasticService.IsBroadcastDeviceId(nodeId))
                {
                    deviceName = "Unknown";
                }
                else
                {
                    var device = await _registrationService.GetDeviceAsync(nodeId);
                    if (device != null)
                    {
                        deviceName = device.NodeName;
                    }
                    else
                    {
                        deviceName = null;
                    }
                }

                sb.AppendLine($"↓↓ SNR {(snr == sbyte.MinValue ? "?" : MeshtasticService.UnroundSnrFromTrace(snr).ToString())} dB");
                sb.AppendLine(deviceName ?? MeshtasticService.GetMeshtasticNodeHexId(nodeId));
            }
            sb.AppendLine($"↓↓ SNR ? dB");
            sb.AppendLine(_options.MeshtasticNodeNameLong);
            return sb.ToString();
        }

        public async Task ProcessInboundMeshtasticMessage(MeshMessage message, Device device)
        {
            if (message.MessageType == MeshMessageType.NodeInfo)
            {
                var nodeInfo = (NodeInfoMessage)message;
                await _registrationService.SetDeviceAsync(
                    message.DeviceId,
                    nodeInfo.NodeName,
                    nodeInfo.PublicKey);
                return;
            }
            else if (message.MessageType == MeshMessageType.EncryptedDirectMessage)
            {
                _meshtasticService.NakNoPubKeyMeshtasticMessage(message);
            }
            else if (message.MessageType == MeshMessageType.TraceRoute)
            {
                var traceRouteMsg = (TraceRouteMessage)message;
                _meshtasticService.SendTraceRouteResponse(traceRouteMsg);
                device ??= await _registrationService.GetDeviceAsync(message.DeviceId);
                if (device == null)
                {
                    return;
                }
                var text = await FormatTraceRouteMessage(traceRouteMsg);

                var registrations = await _registrationService.GetRegistrationsByDeviceId(message.DeviceId);
                foreach (var reg in registrations)
                {
                    await _botClient.SendMessage(
                        reg.ChatId,
                        $"{device.NodeName} Trace\r\n" + text);
                }
            }
            else if (message.MessageType == MeshMessageType.Text)
            {
                if (device == null)
                {
                    throw new ArgumentNullException(nameof(device), "Device cannot be null for Text messages");
                }

                _logger.LogDebug("Processing inbound Meshtastic message: {Message}", message);

                var registrations = await _registrationService.GetRegistrationsByDeviceId(message.DeviceId);
                if (registrations.Count == 0)
                {
                    _meshtasticService.AckMeshtasticMessage(device.PublicKey, message);
                    _meshtasticService.SendTextMessage(
                        message.DeviceId,
                        device.PublicKey,
                        $"{StringHelper.Truncate(device.NodeName, 20)} is not registered with {_options.TelegramBotUserName} (Telegram)");
                    return;
                }

                var text = ((TextMessage)message).Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    _logger.LogWarning("Received empty text message from device {DeviceId}", message.DeviceId);
                    return;
                }

                foreach (var reg in registrations)
                {
                    await _botClient.SendMessage(
                        reg.ChatId,
                        $"{device.NodeName}: {text}");
                }

                _meshtasticService.AckMeshtasticMessage(device.PublicKey, message);
            }
        }

        public async Task ProcessAckMessages(List<AckMessage> batch)
        {
            foreach (var item in batch)
            {
                if (item.Success)
                {
                    await SetAckMeshMessageStatus(item.AckedMessageId, item.DeviceId);
                }
                else
                {
                    await UpdateMeshMessageStatus(item.AckedMessageId, DeliveryStatus.Failed);
                }
            }
        }

        public async Task ProcessMessageSent(long meshtasticMessageId)
        {
            await UpdateMeshMessageStatus(
                meshtasticMessageId,
                DeliveryStatus.SentToMqtt,
                maxCurrentStatus: DeliveryStatus.Queued);
        }
    }
}
