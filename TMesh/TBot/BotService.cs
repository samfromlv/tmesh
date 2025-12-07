using Meshtastic.Protobufs;
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
    public class BotService(
        TelegramBotClient botClient,
        IOptions<TBotOptions> options,
        RegistrationService registrationService,
        MeshtasticService meshtasticService,
        IMemoryCache memoryCache,
        ILogger<BotService> logger)
    {

        const int TrimUserNamesToLength = 8;
        private readonly TBotOptions _options = options.Value;

        public List<MeshtasticMessageStatus> TrackedMessages { get; } = [];

        public async Task InstallWebhook()
        {
            await botClient.SetWebhook(
                _options.TelegramUpdateWebhookUrl,
                allowedUpdates: [UpdateType.Message],
                secretToken: _options.TelegramWebhookSecret);

            await botClient.SetMyCommands(
            [
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
            ]);
        }

        public async Task<WebhookInfo> CheckInstall()
        {
            return await botClient.GetWebhookInfo();
        }

        private void StoreTelegramMessageStatus(long chatId, int messageId, MeshtasticMessageStatus status)
        {
            var currentDelay = meshtasticService.EstimateDelay(MessagePriority.Normal);
            var cacheKey = $"TelegramMessageStatus_{chatId}_{messageId}";
            memoryCache.Set(cacheKey, status, currentDelay.Add(TimeSpan.FromMinutes(Math.Max(currentDelay.TotalMinutes * 1.3, 3))));
            TrackedMessages.Add(status);
        }

        public MeshtasticMessageStatus GetTelegramMessageStatus(long chatId, int messageId)
        {
            var cacheKey = $"TelegramMessageStatus_{chatId}_{messageId}";
            if (memoryCache.TryGetValue(cacheKey, out MeshtasticMessageStatus status))
            {
                return status;
            }
            return null;
        }

        private void StoreMeshMessageStatus(long meshtasticMessageId, MeshtasticMessageStatus status)
        {
            var currentDelay = meshtasticService.EstimateDelay(MessagePriority.Normal);
            var cacheKey = $"MeshtasticMessageStatus_{meshtasticMessageId}";
            memoryCache.Set(cacheKey, status, currentDelay.Add(TimeSpan.FromMinutes(Math.Max(currentDelay.TotalMinutes * 1.3, 3))));
        }

        private MeshtasticMessageStatus GetMeshMessageStatus(long meshtasticMessageId)
        {
            var cacheKey = $"MeshtasticMessageStatus_{meshtasticMessageId}";
            if (memoryCache.TryGetValue(cacheKey, out MeshtasticMessageStatus status))
            {
                return status;
            }
            return null;
        }

        private void StoreDeviceGateway(MeshMessage msg)
            => StoreDeviceGateway(msg.DeviceId, msg.GatewayId);

        private void StoreDeviceGateway(long deviceId, long gatewayId)
        {
            if (_options.DirectGatewayRoutingSeconds <= 0)
            {
                return;
            }

            var cacheKey = $"DeviceGateway_{deviceId}";
            memoryCache.Set(cacheKey, gatewayId, DateTime.UtcNow.AddSeconds(_options.DirectGatewayRoutingSeconds));
        }

        private long? GetDeviceGateway(long deviceId)
        {
            if (_options.DirectGatewayRoutingSeconds <= 0)
            {
                return null;
            }
            var cacheKey = $"DeviceGateway_{deviceId}";
            if (memoryCache.TryGetValue(cacheKey, out long gatewayId))
            {
                return gatewayId;
            }
            return null;
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


            var chatState = registrationService.GetChatState(userId, chatId);

            switch (chatState)
            {
                case ChatState.Adding_NeedDeviceId:
                case ChatState.Adding_NeedCode:
                    await HandleDeviceAdd(userId, chatId, msg, chatState.Value);
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
            long chatId,
            Message message,
            ChatState chatState)
        {
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "Registration canceled.");
                registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            if (chatState == ChatState.Adding_NeedDeviceId)
            {
                await ProcessNeedDeviceId(userId, chatId, message);
            }
            else if (chatState == ChatState.Adding_NeedCode)
            {
                await ProcessNeedCode(userId, chatId, message);
            }
            else
            {
                logger.LogWarning("Unexpected chat state {ChatState} in HandleDeviceAdd", chatState);
            }
        }

        private async Task HandleDeviceRemove(long userId, long chatId, Message message)
        {
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "Removal canceled.");
                registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await botClient.SendMessage(chatId, "Please send a Meshtastic device ID to remove or /stop to cancel.");
                return;
            }
            if (!MeshtasticService.TryParseDeviceId(text, out var deviceId))
            {
                await botClient.SendMessage(chatId, "Invalid device ID format. The device ID can be decimal or hex (hex starts with ! or #). Send /stop to cancel.");
                return;
            }

            var removed = await registrationService.RemoveDeviceFromChatAsync(chatId, deviceId);
            if (!removed)
            {
                await botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} is not registered in this chat.");
            }
            else
            {
                await botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} removed from this chat.");
            }
            registrationService.SetChatState(userId, chatId, ChatState.Default);
        }

        private DateTime EstimateSendDelay(int messageCount)
        {
            var delay = meshtasticService.EstimateDelay(MessagePriority.Normal);
            return DateTime.UtcNow
                .Add(delay)
                .Add(meshtasticService.SingleMessageQueueDelay * (messageCount - 1));
        }

        public async Task UpdateMeshMessageStatus(
            long meshMessageId,
            DeliveryStatus newStatus,
            DeliveryStatus? maxCurrentStatus = null)
        {
            var status = GetMeshMessageStatus(meshMessageId);
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

        private async Task ReportStatus(MeshtasticMessageStatus status)
        {
            if (status.MeshMessages.All(x => x.Value.Status == DeliveryStatus.Delivered)
                || status.MeshMessages.All(x => x.Value.Status == DeliveryStatus.Unknown)
                || status.MeshMessages.All(x => x.Value.Status == DeliveryStatus.Failed))
            {
                var deliveryStatus = status.MeshMessages.First().Value;
                string reactionEmoji = ConvertDeliveryStatusToString(deliveryStatus.Status);

                await botClient.SetMessageReaction(
                          status.TelegramChatId,
                          status.TelegramMessageId,
                          [reactionEmoji]);

                int? deletedReplyId = status.BotReplyId;
                if (deletedReplyId != null)
                {
                    await botClient.DeleteMessage(
                        status.TelegramChatId,
                        deletedReplyId.Value);
                    if (deletedReplyId == status.BotReplyId)
                    {
                        status.BotReplyId = null;
                    }
                }
            }
            else
            {
                var sb = new StringBuilder("Status: ");
                var statusesOrdered = status.MeshMessages.OrderBy(x => x.Value.DeviceId).ToList();
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
                    await botClient.EditMessageText(
                        status.TelegramChatId,
                        status.BotReplyId.Value,
                        sb.ToString());
                }
                else
                {
                    var replyMsg = await botClient.SendMessage(
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

        private static string ConvertDeliveryStatusToString(DeliveryStatus status)
        {
            return status switch
            {
                DeliveryStatus.Created => ReactionEmoji.WritingHand,
                DeliveryStatus.Queued => ReactionEmoji.Eyes,
                DeliveryStatus.SentToMqtt => ReactionEmoji.Dove,
                DeliveryStatus.Unknown => ReactionEmoji.ManShrugging,
                DeliveryStatus.Delivered => ReactionEmoji.OkHand,
                DeliveryStatus.Failed => ReactionEmoji.ThumbsDown,
                _ => ReactionEmoji.ExplodingHead,
            };
        }

        private async Task ProcessNeedDeviceId(long userId, long chatId, Message message)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && MeshtasticService.TryParseDeviceId(message.Text, out var deviceId))
            {
                await ProcessDeviceIdForAdd(userId, chatId, deviceId);
            }
            else
            {
                await botClient.SendMessage(chatId, "Invalid device ID. Please send a valid Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #). Send /stop to cancel.");
            }
        }

        private async Task ProcessDeviceIdForAdd(long userId, long chatId, long deviceId)
        {
            var device = await registrationService.GetDeviceAsync(deviceId);
            if (device == null)
            {
                await botClient.SendMessage(chatId,
                    $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} has not yet been seen by the MQTT node {_options.MeshtasticNodeNameLong} in the Meshtastic network.\r\n" +
                    $"1. Ensure your primary channel is '{_options.MeshtasticPrimaryChannelName}' and the key is '{_options.MeshtasticPrimaryChannelPskBase64}'.\r\n" +
                    "2. Make sure 'OK to MQTT' is enabled in LoRa settings on your device.\r\n" +
                    $"3. Find node {_options.MeshtasticNodeNameLong} (MQTT) in node list, open it and click on 'Exchange user information'. {_options.MeshtasticNodeNameLong} broadcasts it's node info every {_options.SentTBotNodeInfoEverySeconds} seconds.\r\n\r\n" +
                    "Registration aborted.");
                registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            if (await registrationService.HasRegistrationAsync(chatId, deviceId))
            {
                await botClient.SendMessage(chatId, $"Device {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(deviceId)}) is already registered in this chat. Registration aborted.");
                registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            var codesSent = registrationService.IncrementDeviceCodesSentRecently(deviceId);
            if (codesSent > RegistrationService.MaxCodeVerificationTries)
            {
                await botClient.SendMessage(chatId, $"Device {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(deviceId)}) has reached the maximum number of verification codes sent. Please wait at least 1 hour before trying again to add the same device to any chats. Registration aborted.");
                registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            var code = RegistrationService.GenerateRandomCode();
            registrationService.StorePendingCodeAsync(userId, chatId, deviceId, code, DateTimeOffset.UtcNow.AddMinutes(5));

            var msg = await botClient.SendMessage(chatId,
                $"Verification code sent to device {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(deviceId)}). Please reply with the received code here. The code is valid for 5 minutes.");

            await SendAndTrackMeshtasticMessage(
                device,
                chatId,
                msg.MessageId,
                $"TMesh verification code is: {code}");

            registrationService.SetChatState(userId, chatId, Models.ChatState.Adding_NeedCode);
        }

        private string ExtractDeviceIdFromCommand(string commandText, string command)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return null;
            }

            // Remove the command part and trim
            var text = commandText[command.Length..].Trim();
            if (text == $"@{_options.TelegramBotUserName}")
            {
                return null;
            }

            // Return null if empty (no device ID provided)
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // Extract first word (device ID should not have spaces)
            var parts = text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : null;
        }


        private Task SendAndTrackMeshtasticMessage(
            IDeviceKey device,
            long chatId,
            int messageId,
            string text)
        {
            return SendAndTrackMeshtasticMessages(
                [device],
                chatId,
                messageId,
                null,
                text);
        }

        public async Task ResolveMessageStatus(long chatId, int telegramMessageId)
        {
            var status = GetTelegramMessageStatus(chatId, telegramMessageId);
            if (status != null)
            {
                bool madeChanged = false;
                lock (status)
                {
                    foreach (var msg in status.MeshMessages.Values)
                    {
                        if (msg.Status == DeliveryStatus.SentToMqtt)
                        {
                            msg.Status = DeliveryStatus.Unknown;
                            madeChanged = true;
                        }
                    }
                }
                if (madeChanged)
                {
                    await ReportStatus(status);
                }
            }
        }

        private async Task SendAndTrackMeshtasticMessages(
            IEnumerable<IDeviceKey> devices,
            long chatId,
            int messageId,
            int? replyToTelegramMsgId,
            string text)
        {
            var status = new MeshtasticMessageStatus
            {
                TelegramChatId = chatId,
                TelegramMessageId = messageId,
                MeshMessages = [],
                EstimatedSendDate = EstimateSendDelay(devices.Count())
            };

            var deviceMessageIds = new List<(long deviceId, long messageId, byte[] publicKey)>();
            foreach (var device in devices)
            {
                var newMeshMessageId = MeshtasticService.GetNextMeshtasticMessageId();
                status.MeshMessages.Add(newMeshMessageId, new DeliveryStatusWithDeviceId
                {
                    DeviceId = device.DeviceId,
                    Status = DeliveryStatus.Queued
                });
                StoreMeshMessageStatus(newMeshMessageId, status);
                deviceMessageIds.Add((device.DeviceId, newMeshMessageId, device.PublicKey));
            }
            StoreTelegramMessageStatus(chatId, messageId, status);

            await ReportStatus(status);

            var queueResults = new List<QueueResult>();

            var replyToStatus = replyToTelegramMsgId.HasValue
                ? GetTelegramMessageStatus(chatId, replyToTelegramMsgId.Value)
                : null;

            foreach (var (deviceId, newMeshMessageId, publicKey) in deviceMessageIds)
            {
                var replyToMeshMessageId = replyToStatus?.MeshMessages
                    .FirstOrDefault(kv => kv.Value.DeviceId == deviceId).Key;

                var gatewayId = GetDeviceGateway(deviceId);

                queueResults.Add(
                    meshtasticService.SendTextMessage(
                        newMeshMessageId,
                        deviceId,
                        publicKey,
                        text,
                        replyToMeshMessageId,
                        gatewayId,
                        hopLimit: int.MaxValue));
            }
        }

        private async Task ProcessNeedCode(long userId, long chatId, Message message)
        {
            var maybeCode = message.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(maybeCode)
                  && RegistrationService.IsValidCodeFormat(maybeCode))
            {
                if (await registrationService.TryCreateRegistrationWithCode(
                    userId,
                    chatId,
                    maybeCode))
                {
                    await botClient.SendMessage(chatId, "Registration successful.");
                    registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                    return;
                }
                else
                {
                    await botClient.SendMessage(chatId, "Invalid or expired code. Please check it and try again, or cancel with /stop.");
                    return;
                }
            }
            else
            {
                await botClient.SendMessage(chatId, "Invalid code format. Please send the 6-digit verification code sent to your Meshtastic device. Send /stop to cancel.");
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
                await StartAdd(userId, chatId, deviceIdFromCommand);
                return;
            }
            if (message.Text?.StartsWith("/remove", StringComparison.OrdinalIgnoreCase) == true)
            {
                var deviceIdFromCommand = ExtractDeviceIdFromCommand(message.Text, "/remove");
                await StartRemove(userId, chatId, deviceIdFromCommand);
                return;
            }
            if (message.Text?.StartsWith("/status", StringComparison.OrdinalIgnoreCase) == true)
            {
                await HandleStatus(chatId);
                return;
            }
            if (message.Text?.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) == true)
            {
                var handled = await HandleAdmin(
                    userId,
                    chatId,
                    message.Text);

                if (handled)
                {
                    return;
                }
            }

            await HandleText(
                message.MessageId,
                userName,
                chatId,
                message.ReplyToMessage?.MessageId,
                message.Text ?? string.Empty);
        }

        private async Task<bool> HandleAdmin(
            long userId,
            long chatId,
            string text)
        {
            var chatState = registrationService.GetChatState(userId, chatId);

            var noPrefix = text["/admin".Length..].Trim();
            var segments = noPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                if (chatState == ChatState.Admin)
                {
                    await botClient.SendMessage(chatId, "Invalid admin command");
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else if (chatState == ChatState.Default && segments[0] != "login")
            {
                return false;
            }

            var command = segments[0].ToLowerInvariant();

            switch (command)
            {
                case "login":
                    {
                        if (chatState == ChatState.Admin)
                        {
                            await botClient.SendMessage(chatId, "You are already logged in as admin.");
                            return true;
                        }
                        var password = segments.Length >= 2 ? segments[1] : string.Empty;
                        if (registrationService.TryAdminLogin(userId, password))
                        {
                            registrationService.SetChatState(userId, chatId, Models.ChatState.Admin);
                            await botClient.SendMessage(chatId, "Admin access granted.");
                        }
                        else
                        {
                            await botClient.SendMessage(chatId, "Invalid admin password.");
                        }
                        return true;
                    }
                case "logout":
                case "exit":
                case "quit":
                case "stop":
                    {
                        registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                        await botClient.SendMessage(chatId, "Admin access revoked.");
                        return true;
                    }
                case "public_text_primary":
                    {
                        var announcement = noPrefix["public_text_primary".Length..].Trim();
                        if (string.IsNullOrWhiteSpace(announcement))
                        {
                            await botClient.SendMessage(chatId, "Announcement text cannot be empty.");
                            return true;
                        }
                        if (!MeshtasticService.CanSendMessage(announcement))
                        {
                            await botClient.SendMessage(
                                chatId,
                                $"Announcement is too long to send to a Meshtastic device. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes (English letters: 1 byte, Cyrillic: 2 bytes, emoji: 4 bytes).");
                            return true;
                        }
                        meshtasticService.SendPublicTextMessage(announcement, relayGatewayId: null, hopLimit: int.MaxValue);
                        await botClient.SendMessage(chatId, $"Announcement sent to {_options.MeshtasticPrimaryChannelName}.");
                        return true;
                    }
                case "public_text":
                    {
                        var cmd = noPrefix["public_text".Length..].Trim();
                        var channelNameEndIndex = cmd.IndexOf(' ');
                        if (channelNameEndIndex == -1)
                        {
                            await botClient.SendMessage(chatId, "Please specify the channel name and announcement text.");
                            return true;
                        }
                        var channelName = cmd[..channelNameEndIndex].Trim();
                        if (!meshtasticService.IsPublicChannelConfigured(channelName))
                        {
                            await botClient.SendMessage(chatId, $"Channel '{channelName}' is not configured as a public channel.");
                            return true;
                        }
                        var announcement = cmd[channelNameEndIndex..].Trim();
                        if (string.IsNullOrWhiteSpace(announcement))
                        {
                            await botClient.SendMessage(chatId, "Announcement text cannot be empty.");
                            return true;
                        }
                        if (!MeshtasticService.CanSendMessage(announcement))
                        {
                            await botClient.SendMessage(
                                chatId,
                                $"Announcement is too long to send to a Meshtastic device. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes (English letters: 1 byte, Cyrillic: 2 bytes, emoji: 4 bytes).");
                            return true;
                        }
                        meshtasticService.SendPublicTextMessage(announcement, relayGatewayId: null, hopLimit: int.MaxValue);
                        await botClient.SendMessage(chatId, $"Announcement sent to {_options.MeshtasticPrimaryChannelName}.");
                        return true;
                    }
                case "text":
                    {
                        var toDeviceID = segments.Length >= 2 ? segments[1] : string.Empty;
                        var announcement = noPrefix[("text " + toDeviceID).Length..].Trim();
                        if (string.IsNullOrWhiteSpace(toDeviceID))
                        {
                            await botClient.SendMessage(chatId, "Please specify the target device ID.");
                            return true;
                        }
                        if (!MeshtasticService.TryParseDeviceId(toDeviceID, out var parsedDeviceId))
                        {
                            await botClient.SendMessage(chatId, $"Invalid device ID format: '{toDeviceID}'. The device ID can be decimal or hex (hex starts with ! or #).");
                            return true;
                        }
                        if (string.IsNullOrWhiteSpace(announcement))
                        {
                            await botClient.SendMessage(chatId, "Message text cannot be empty.");
                            return true;
                        }
                        if (!MeshtasticService.CanSendMessage(announcement))
                        {
                            await botClient.SendMessage(
                                chatId,
                                $"Message is too long to send to a Meshtastic device. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes (English letters: 1 byte, Cyrillic: 2 bytes, emoji: 4 bytes).");
                            return true;
                        }
                        var device = await registrationService.GetDeviceAsync(parsedDeviceId);
                        if (device == null)
                        {
                            await botClient.SendMessage(chatId, $"Device {toDeviceID} not found.");
                            return true;
                        }

                        var msg = await botClient.SendMessage(chatId, $"Sending message to device {toDeviceID}...");

                        await SendAndTrackMeshtasticMessage(
                            device,
                            chatId,
                            msg.Id,
                            announcement);
                        return true;

                    }
                case "nodeinfo":
                    {
                        var nodeId = segments.Length >= 2 ? segments[1] : string.Empty;

                        if (string.IsNullOrWhiteSpace(nodeId)
                            || !MeshtasticService.TryParseDeviceId(nodeId, out var parsedNodeId))
                        {
                            await botClient.SendMessage(chatId, $"Invalid node ID format: '{nodeId}'. The node ID can be decimal or hex (hex starts with ! or #).");
                            return true;
                        }

                        var device = await registrationService.GetDeviceAsync(parsedNodeId);
                        if (device == null)
                        {
                            await botClient.SendMessage(chatId, "Not found.");
                            return true;
                        }

                        var json = JsonSerializer.Serialize(device);

                        await botClient.SendMessage(
                            chatId,
                            $"Found node:\r\n\r\n" +
                            json);

                        return true;
                    }

                default:
                    {
                        await botClient.SendMessage(chatId, $"Unknown admin command: {command}");
                        return true;
                    }
            }
        }

        private async Task HandleText(
            int msgId,
            string userName,
            long chatId,
            int? replyToTelegramMessageId,
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var textToMesh = $"{StringHelper.Truncate(userName, TrimUserNamesToLength)}: {text}";

            if (!MeshtasticService.CanSendMessage(text))
            {
                await botClient.SendMessage(
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

            var registrations = await registrationService.GetDeviceKeysByChatIdCached(chatId);
            if (registrations.Count == 0)
            {
                await botClient.SendMessage(
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
                replyToTelegramMessageId,
                textToMesh);
        }

        private async Task HandleStatus(long chatId)
        {
            var devices = await registrationService.GetDeviceNamesByChatId(chatId);
            if (devices.Count == 0)
            {
                await botClient.SendMessage(chatId, "No registered devices. You can register a new device with the /add command.");
            }
            else
            {
                var now = DateTime.UtcNow;
                var lines = devices.Select(d => $"• Device: {d.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId)}), last seen {FormatTimeSpan(now - d.LastSeen)} ago");
                var text = "Registered devices:\r\n" + string.Join("\r\n", lines);
                await botClient.SendMessage(chatId, text);
            }
        }

        static string FormatTimeSpan(TimeSpan ts)
        {
            return ts.Days > 0
                ? ts.ToString(@"d\:hh\:mm\:ss")
                : ts.ToString(@"hh\:mm\:ss");
        }

        private async Task StartAdd(long userId, long chatId, string deviceIdText)
        {
            if (string.IsNullOrWhiteSpace(deviceIdText))
            {
                // No device ID provided, ask for it
                await botClient.SendMessage(chatId, "Please send your Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #).");
                registrationService.SetChatState(userId, chatId, Models.ChatState.Adding_NeedDeviceId);
                return;
            }

            // Device ID provided in command, process immediately
            if (!MeshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await botClient.SendMessage(chatId,
                    $"Invalid device ID format: '{deviceIdText}'. The device ID can be decimal or hex (hex starts with ! or #).\n\n" +
                    "Examples:\n" +
                    "• /add 123456789\n" +
                    "• /add !75bcd15\n" +
                    "• /add #75bcd15\n\n" +
                    "Or use /add without parameters and I'll ask for the device ID.");
                return;
            }

            // Process the device ID (same logic as ProcessNeedDeviceId)
            await ProcessDeviceIdForAdd(userId, chatId, deviceId);
        }

        private async Task StartRemove(long userId, long chatId, string deviceIdText)
        {
            if (string.IsNullOrWhiteSpace(deviceIdText))
            {
                // No device ID provided, ask for it
                await botClient.SendMessage(chatId, "Please send your Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #).");
                registrationService.SetChatState(userId, chatId, Models.ChatState.RemovingDevice);
                return;
            }

            // Device ID provided in command, process immediately
            if (!MeshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await botClient.SendMessage(chatId,
                    $"Invalid device ID format: '{deviceIdText}'. The device ID can be decimal or hex (hex starts with ! or #).\n\n" +
                    "Examples:\n" +
                    "• /remove 123456789\n" +
                    "• /remove !75bcd15\n" +
                    "• /remove #75bcd15\n\n" +
                    "Or use /remove without parameters and I'll ask for the device ID.");
                return;
            }

            // Process removal immediately
            var removed = await registrationService.RemoveDeviceFromChatAsync(chatId, deviceId);
            if (!removed)
            {
                await botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} is not registered in this chat.");
            }
            else
            {
                await botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} has been removed from this chat.");
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
            logger.LogDebug("Processing inbound Telegram message: {Payload}", payload);
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
                if (MeshtasticService.IsBroadcastDeviceId(nodeId))
                {
                    deviceName = "Unknown";
                }
                else
                {
                    var device = await registrationService.GetDeviceAsync(nodeId);
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


        public async Task ProcessInboundMeshtasticMessage(MeshMessage message, Device deviceOrNull)
        {
            StoreDeviceGateway(message);
            switch (message.MessageType)
            {
                case MeshMessageType.NodeInfo:
                    await ProcessInboundNodeInfo((NodeInfoMessage)message);
                    break;
                case MeshMessageType.Text:
                    await ProcessInboundMeshTextMessage((TextMessage)message, deviceOrNull);
                    break;
                case MeshMessageType.EncryptedDirectMessage:
                    if (message.NeedAck)
                    {
                        meshtasticService.NakNoPubKeyMeshtasticMessage(message, message.GatewayId);
                    }
                    break;
                case MeshMessageType.TraceRoute:
                    await ProcessInboundTraceRoute((TraceRouteMessage)message, deviceOrNull);
                    break;
                case MeshMessageType.Position:
                    await ProcessInboundPositionMessage((PositionMessage)message, deviceOrNull);
                    break;
                case MeshMessageType.AckMessage:
                default:
                    logger.LogWarning("Received unsupported Meshtastic message type: {MessageType}", message.MessageType);
                    break;
            }
        }

        private async Task ProcessInboundPositionMessage(PositionMessage message, Device deviceOrNull)
        {
            if (message.NeedAck)
            {
                meshtasticService.AckMeshtasticMessage(deviceOrNull.PublicKey, message, message.GatewayId);
            }
            deviceOrNull ??= await registrationService.GetDeviceAsync(message.DeviceId);

            if (deviceOrNull == null)
            {
                throw new ArgumentNullException(nameof(deviceOrNull), "Device cannot be null for Text messages");
            }
            logger.LogDebug("Processing inbound Meshtastic message: {Message}", message);
            var chatIds = await registrationService.GetChatsByDeviceIdCached(message.DeviceId);
            if (chatIds.Count == 0)
            {
                SendNotRegisteredResponse(message, deviceOrNull);
                return;
            }

            foreach (var chatId in chatIds)
            {
                await botClient.SendMessage(
                    chatId,
                    $"{deviceOrNull.NodeName} sent a location:");
                await botClient.SendLocation(
                    chatId,
                    message.Latitude,
                    message.Longitude,
                    heading: message.HeadingDegrees,
                    horizontalAccuracy: Math.Min(message.AccuracyMeters, 1500));
            }

        }

        private void SendNotRegisteredResponse(MeshMessage message, Device device)
        {
            var template = _options.Texts.NotRegisteredDeviceReply ??
                "{nodeName} is not registered with {botName} (Telegram)";

            var nodeName = StringHelper.Truncate(device.NodeName, 20);
            var botName = _options.TelegramBotUserName;
            var text = template
                .Replace("{nodeName}", nodeName)
                .Replace("{botName}", botName);

            if (!MeshtasticService.CanSendMessage(text))
            {
                throw new InvalidOperationException("Not registered response text is too long to send to Meshtastic device.");
            }


            meshtasticService.SendTextMessage(
                message.DeviceId,
                device.PublicKey,
                text,
                replyToMessageId: null,
                relayGatewayId: message.GatewayId,
                hopLimit: message.GetSuggestedReplyHopLimit());
        }

        private async Task ProcessInboundMeshTextMessage(TextMessage message, Device deviceOrNull)
        {
            logger.LogDebug("Processing inbound Meshtastic message: {Message}", message);
            if (message.IsDirectMessage)
            {
                await ProcessInboundDirectMeshTextMessage(message, deviceOrNull);
            }
            else
            {
                await ProcessInboundPublicMeshTextMessage(message, deviceOrNull);
            }

        }

        private async Task ProcessInboundPublicMeshTextMessage(TextMessage message, Device deviceOrNull)
        {
            if (!_options.ReplyToPublicPingsViaDirectMessage
                && !_options.PingWords.Any())
            {
                return;
            }

            var text = message.Text.Trim();
            bool isPing = _options.PingWords.Any(pingWord => string.Equals(text, pingWord, StringComparison.OrdinalIgnoreCase));
            if (!isPing || message.ReplyTo != 0)
            {
                return;
            }

            deviceOrNull ??= await registrationService.GetDeviceAsync(message.DeviceId);

            if (deviceOrNull == null)
            {
                meshtasticService.NakNoPubKeyMeshtasticMessage(
                    message,
                    message.GatewayId);
                return;
            }

            meshtasticService.SendTextMessage(
                message.DeviceId,
                deviceOrNull.PublicKey,
                _options.Texts.PingReply ?? "pong",
                replyToMessageId: null,//Message is from public channel and we are sending direct reply, so no replyToMessageId
                relayGatewayId: message.GatewayId,
                hopLimit: message.GetSuggestedReplyHopLimit());

            meshtasticService.AddStat(new Shared.Models.MeshStat
            {
                PongSent = 1,
            });
        }

        private async Task ProcessInboundDirectMeshTextMessage(TextMessage message, Device deviceOrNull)
        {
            if (deviceOrNull == null)
            {
                throw new ArgumentNullException(nameof(deviceOrNull), "Device cannot be null for Text messages");
            }
            if (message.NeedAck)
            {
                meshtasticService.AckMeshtasticMessage(
                    deviceOrNull.PublicKey,
                    message,
                    message.GatewayId);
            }

            string cmdText = null;
            if (message.Text != null
                && message.Text.Trim().Length > 1
                && message.Text.StartsWith('/'))
            {
                cmdText = message.Text.Substring(1).Trim();
            }

            if (cmdText != null &&
                _options.PingWords.Any(pingWord => string.Equals(cmdText, pingWord, StringComparison.OrdinalIgnoreCase)))
            {
                meshtasticService.SendTextMessage(
                    message.DeviceId,
                    deviceOrNull.PublicKey,
                    _options.Texts.PingReply ?? "pong",
                    replyToMessageId: message.Id,
                    relayGatewayId: message.GatewayId,
                    hopLimit: message.GetSuggestedReplyHopLimit());

                meshtasticService.AddStat(new Shared.Models.MeshStat
                {
                    PongSent = 1,
                });
                return;
            }
            var chatIds = await registrationService.GetChatsByDeviceIdCached(message.DeviceId);
            if (chatIds.Count == 0)
            {
                SendNotRegisteredResponse(message, deviceOrNull);
                return;
            }


            var text = message.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("Received empty text message from device {DeviceId}", message.DeviceId);
                return;
            }

            bool sentReply = false;

            if (message.ReplyTo != 0)
            {
                var replyToStatus = GetMeshMessageStatus(message.ReplyTo);
                if (replyToStatus != null
                        && chatIds.Any(x => x == replyToStatus.TelegramChatId))
                {
                    var msg = await botClient.SendMessage(
                        replyToStatus.TelegramChatId,
                        $"{deviceOrNull.NodeName}: {text}",
                        replyParameters: new ReplyParameters
                        {
                            AllowSendingWithoutReply = true,
                            ChatId = replyToStatus.TelegramChatId,
                            MessageId = replyToStatus.TelegramMessageId,
                        });

                    var status = new MeshtasticMessageStatus
                    {
                        TelegramChatId = replyToStatus.TelegramChatId,
                        TelegramMessageId = msg.Id,
                        MeshMessages = new Dictionary<long, DeliveryStatusWithDeviceId>
                            {
                                { message.Id, new DeliveryStatusWithDeviceId
                                    {
                                        DeviceId = message.DeviceId,
                                        Status = DeliveryStatus.Delivered,
                                    }
                                }
                            },
                        BotReplyId = null,
                    };

                    StoreTelegramMessageStatus(replyToStatus.TelegramChatId,
                        msg.Id,
                        status);

                    StoreMeshMessageStatus(message.Id, status);

                    sentReply = true;
                }
            }

            if (!sentReply)
            {
                foreach (var chatId in chatIds)
                {
                    var msg = await botClient.SendMessage(
                        chatId,
                        $"{deviceOrNull.NodeName}: {text}");

                    var status = new MeshtasticMessageStatus
                    {
                        TelegramChatId = chatId,
                        TelegramMessageId = msg.Id,
                        MeshMessages = new Dictionary<long, DeliveryStatusWithDeviceId>
                            {
                                { message.Id, new DeliveryStatusWithDeviceId
                                    {
                                        DeviceId = message.DeviceId,
                                        Status = DeliveryStatus.Delivered,
                                    }
                                }
                            },
                        BotReplyId = null
                    };

                    StoreTelegramMessageStatus(chatId, msg.Id, status);
                    StoreMeshMessageStatus(message.Id, status);
                }
            }
        }

        private async Task ProcessInboundTraceRoute(TraceRouteMessage message, Device deviceOrNull)
        {
            meshtasticService.SendTraceRouteResponse(message, message.GatewayId);
            deviceOrNull ??= await registrationService.GetDeviceAsync(message.DeviceId);
            if (deviceOrNull == null)
            {
                return;
            }
            var text = await FormatTraceRouteMessage(message);

            var chatIds = await registrationService.GetChatsByDeviceIdCached(message.DeviceId);
            foreach (var chatId in chatIds)
            {
                await botClient.SendMessage(
                    chatId,
                    $"{deviceOrNull.NodeName} trace\r\n" + text);
            }
        }

        private async Task ProcessInboundNodeInfo(NodeInfoMessage message)
        {
            if (message.NeedAck)
            {
                meshtasticService.AckMeshtasticMessage(
                    message.PublicKey,
                    message,
                    message.GatewayId);
            }

            await registrationService.SetDeviceAsync(
                message.DeviceId,
                message.NodeName,
                message.PublicKey);
        }

        public async Task ProcessAckMessages(List<AckMessage> batch)
        {
            foreach (var item in batch)
            {
                StoreDeviceGateway(item);
                await UpdateMeshMessageStatus(item.AckedMessageId,
                    item.Success
                    ? DeliveryStatus.Delivered
                    : DeliveryStatus.Failed);
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
