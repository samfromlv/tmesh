using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using System.Text;
using System.Text.Json;
using TBot.Analytics;
using TBot.Analytics.Models;
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
        ILogger<BotService> logger,
        IServiceProvider services)
    {

        const int TrimUserNamesToLength = 8;
        private const string NoDeviceOrChannelMessage = "No registered devices or channels. You can register a new device with the /add_device command or channel with /add_channel command. Please remove the bot from the group if you don't need it.";
        private const string NoDeviceMessage = "No registered devices. You can register a new device with the /add_device command.";
        private readonly TBotOptions _options = options.Value;
        private static readonly JsonSerializerOptions IdentedOptions = new()
        {
            WriteIndented = true,
        };

        public List<MeshtasticMessageStatus> TrackedMessages { get; } = [];
        public bool GatewayListChanged { get; private set; }

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
                    Command = "add_device",
                    Description = "Register a Meshtastic device (e.g., /add_device !aabbcc11)"
                },
                new BotCommand
                {
                    Command = "add_channel",
                    Description = "Register a Meshtastic private channel (e.g., /add_channel !aabbcc11)"
                },
                new BotCommand
                {
                    Command = "remove_device",
                    Description = "Unregister a Meshtastic device (e.g., /remove_device !aabbcc11)"
                },
                new BotCommand
                {
                    Command = "remove_channel",
                    Description = "Unregister a Meshtastic private channel (e.g., /remove_channel <ChannelID>). Get registered channel ID with /status command or run /remove_channel without params to see registered channel IDs."
                },
                new BotCommand
                {
                    Command = "remove_device_from_all_chats",
                    Description = "Unregister a Meshtastic device from all chats. Useful when device changes owner or you have no access to chats where device is registered. (e.g., /remove_from_all_chats !aabbcc11)"
                },
                new BotCommand
                {
                    Command = "remove_channel_from_all_chats",
                    Description = "Unregister a Meshtastic private channel from current chat and all other chats where you have registered it. Use /status to see registered channel IDs. (e.g., /remove_channel_from_all_chats <ChannelID>)"
                },
                new BotCommand
                {
                    Command = "status",
                    Description = "Show list of registered Meshtastic devices, supports filter by name (e.g. /status MyDevice)"
                },
                new BotCommand
                {
                    Command = "position",
                    Description = "Show last known position of registered Meshtastic devices as map, supports filter by name (e.g. /position MyDevice)"
                }
            ]);
        }

        public async Task<WebhookInfo> CheckInstall()
        {
            return await botClient.GetWebhookInfo();
        }



        private void StoreTelegramMessageStatus(
            long chatId,
            int messageId,
            MeshtasticMessageStatus status,
            bool trackForStatusResolve = false)
        {
            var currentDelay = meshtasticService.EstimateDelay(MessagePriority.Normal);
            var cacheKey = $"TelegramMessageStatus_{chatId}_{messageId}";
            memoryCache.Set(cacheKey, status, currentDelay.Add(TimeSpan.FromMinutes(Math.Max(currentDelay.TotalMinutes * 1.3, 3))));
            if (trackForStatusResolve)
            {
                TrackedMessages.Add(status);
            }
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
        {
            StoreDeviceGateway(msg.DeviceId, msg.GatewayId, msg.GetSuggestedReplyHopLimit());
        }

        private void StoreDeviceGateway(long deviceId, long gatewayId, int replyHopLimit)
        {
            if (_options.DirectGatewayRoutingSeconds <= 0)
            {
                return;
            }

            var cacheKey = $"DeviceGateway_{deviceId}";
            memoryCache.Set(cacheKey, new DeviceAndGatewayId
            {
                DeviceId = deviceId,
                GatewayId = gatewayId,
                ReplyHopLimit = replyHopLimit
            }, DateTime.UtcNow.AddSeconds(_options.DirectGatewayRoutingSeconds));
        }

        private DeviceAndGatewayId GetDeviceGateway(long deviceId)
        {
            if (_options.DirectGatewayRoutingSeconds <= 0)
            {
                return null;
            }
            var cacheKey = $"DeviceGateway_{deviceId}";
            if (memoryCache.TryGetValue(cacheKey, out DeviceAndGatewayId gatewayId))
            {
                return gatewayId;
            }
            return null;
        }

        private void StoreChannelGateway(int channelId, long gatewayId, long deviceId, int replyHopLimit)
        {
            if (_options.DirectGatewayRoutingSeconds <= 0)
            {
                return;
            }
            var cacheKey = $"ChannelGateway_{channelId}";
            memoryCache.Set(cacheKey, new DeviceAndGatewayId
            {
                DeviceId = deviceId,
                GatewayId = gatewayId,
                ReplyHopLimit = replyHopLimit
            }, DateTime.UtcNow.AddSeconds(_options.DirectGatewayRoutingSeconds));
        }

        private DeviceAndGatewayId GetSingleDeviceChannelGateway(int channelId)
        {
            if (_options.DirectGatewayRoutingSeconds <= 0)
            {
                return null;
            }
            var cacheKey = $"ChannelGateway_{channelId}";
            if (memoryCache.TryGetValue(cacheKey, out DeviceAndGatewayId deviceAndGatewayId))
            {
                return deviceAndGatewayId;
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


            var chatStateWithData = registrationService.GetChatState(userId, chatId);

            switch (chatStateWithData?.State)
            {
                case ChatState.AddingDevice_NeedId:
                case ChatState.AddingDevice_NeedCode:
                    await ProceedDeviceAdd(userId, chatId, msg, chatStateWithData);
                    break;
                case ChatState.AddingChannel_NeedName:
                case ChatState.AddingChannel_NeedKey:
                case ChatState.AddingChannel_NeedSingleDevice:
                case ChatState.AddingChannel_NeedCode:
                    await ProceedChannelAdd(userId, chatId, msg, chatStateWithData);
                    break;
                case ChatState.RemovingDevice:
                case ChatState.RemovingDeviceFromAll:
                    {
                        await ProceedDeviceRemove(userId, chatId, msg, isRemoveFromAll: chatStateWithData.State == ChatState.RemovingDeviceFromAll);
                        break;
                    }
                case ChatState.RemovingChannel:
                case ChatState.RemovingChannelFromAll:
                    {
                        await ProceedChannelRemove(userId, chatId, msg, isRemoveFromAll: chatStateWithData.State == ChatState.RemovingChannelFromAll);
                        break;
                    }
                default:
                    {
                        await HandleDefaultUpdate(userId, userName, chatId, msg);
                        break;
                    }
            }
        }

        private async Task ProceedChannelAdd(
           long userId,
           long chatId,
           Message message,
           ChatStateWithData chatState)
        {
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "Registration canceled.");
                registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            if (chatState.State == ChatState.AddingChannel_NeedName)
            {
                await ProceedNeedChannelName(userId, chatId, message);
            }
            else if (chatState.State == ChatState.AddingChannel_NeedKey)
            {
                await ProceedNeedChannelKey(userId, chatId, message, chatState);
            }
            else if (chatState.State == ChatState.AddingChannel_NeedSingleDevice)
            {
                await ProceedNeedChannelSingleDevice(userId, chatId, message, chatState);
            }
            else if (chatState.State == ChatState.AddingChannel_NeedCode)
            {
                await ProceedNeedCode(userId, chatId, message);
            }
            else
            {
                logger.LogWarning("Unexpected chat state {ChatState} in HandleDeviceAdd", chatState);
            }
        }

        private async Task ProceedDeviceAdd(
            long userId,
            long chatId,
            Message message,
            ChatStateWithData chatState)
        {
            if (message.Text?.Equals("/stop", StringComparison.OrdinalIgnoreCase) == true)
            {
                await botClient.SendMessage(chatId, "Registration canceled.");
                registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            if (chatState.State == ChatState.AddingDevice_NeedId)
            {
                await ProceedNeedDeviceId(userId, chatId, message);
            }
            else if (chatState.State == ChatState.AddingDevice_NeedCode)
            {
                await ProceedNeedCode(userId, chatId, message);
            }
            else
            {
                logger.LogWarning("Unexpected chat state {ChatState} in HandleDeviceAdd", chatState);
            }
        }

        private async Task ProceedDeviceRemove(long userId, long chatId, Message message, bool isRemoveFromAll)
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

            await ExecuteRemoveDevice(chatId, deviceId, isRemoveFromAll);
            registrationService.SetChatState(userId, chatId, ChatState.Default);
        }

        private async Task ProceedChannelRemove(long userId, long chatId, Message message, bool isRemoveFromAll)
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
                await botClient.SendMessage(chatId, "Please send a Channel ID to remove or /stop to cancel.");
                return;
            }
            if (!int.TryParse(text, out var channelId))
            {
                await botClient.SendMessage(chatId, "Invalid channel ID format. The channel ID must be a valid integer. Send /stop to cancel.");
                return;
            }

            await ExecuteRemoveChannel(chatId, userId, channelId, isRemoveFromAll);
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
                if (status.MeshMessages.TryGetValue(meshMessageId, out var msgStatus))
                {
                    if (maxCurrentStatus.HasValue
                        && msgStatus.Status > maxCurrentStatus.Value)
                    {
                        return;
                    }

                    if (msgStatus.Type == RecipientType.ChannelMulti && newStatus == DeliveryStatus.SentToMqtt)
                    {
                        msgStatus.Status = DeliveryStatus.SentToMqttNoAckExpected;
                    }
                    else
                    {
                        msgStatus.Status = newStatus;
                    }
                }
            }
            await ReportStatus(status);
        }

        private async Task ReportStatus(MeshtasticMessageStatus status)
        {
            if (status.MeshMessages.All(x => x.Value.Status == DeliveryStatus.Delivered)
                || status.MeshMessages.All(x => x.Value.Status == DeliveryStatus.Unknown)
                || status.MeshMessages.All(x => x.Value.Status == DeliveryStatus.SentToMqttNoAckExpected)
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
                var statusesOrdered = status.MeshMessages.OrderBy(x => x.Value.RecipientId).ThenBy(x => x.Key).ToList();
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
                DeliveryStatus.SentToMqttNoAckExpected => ReactionEmoji.Dove,
                DeliveryStatus.Unknown => ReactionEmoji.ManShrugging,
                DeliveryStatus.Delivered => ReactionEmoji.OkHand,
                DeliveryStatus.Failed => ReactionEmoji.ThumbsDown,
                _ => ReactionEmoji.ExplodingHead,
            };
        }

        private async Task ProceedNeedDeviceId(long userId, long chatId, Message message)
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

        private async Task ProceedNeedChannelName(long userId, long chatId, Message message)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && MeshtasticService.IsValidChannelName(message.Text))
            {
                await SendNeedChannelKeyTgMsg(chatId);
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedKey,
                    ChannelName = message.Text
                });
            }
            else
            {
                await botClient.SendMessage(chatId,
                   $"Invalid channel name format: '{message.Text}'. The channel name must be a valid Meshtastic channel name (less than 12 bytes).\n\n" +
                   "Please try again or type /stop to cancell the registration.");
            }
        }

        private async Task ProceedNeedChannelKey(long userId, long chatId, Message message, ChatStateWithData state)
        {
            if (!string.IsNullOrWhiteSpace(message.Text)
                                && MeshtasticService.TryParseChannelKey(message.Text, out var key))
            {
                if (string.IsNullOrEmpty(state.ChannelName)
                    || !MeshtasticService.IsValidChannelName(state.ChannelName))
                {
                    await botClient.SendMessage(chatId,
                        $"Registration data is corrupted, channel name is missing in chat state. Registration process aborted. Please try again.");
                    registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                    return;
                }

                if (meshtasticService.IsPublicChannel(state.ChannelName, key))
                {
                    await botClient.SendMessage(chatId,
                                 $"Adding public, well known channels is not allowed. Registration process aborted. Please try with private channels.");
                    registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                    return;
                }

                await ProcessChannelForAdd(userId, chatId, state.ChannelName, key, message.Text, isSingleDevice: null);
            }
            else
            {
                await botClient.SendMessage(chatId,
                                $"Invalid channel key format: '{message.Text}'. The channel key must be a valid Meshtastic channel key (base64-encoded, 16 or 32 bytes).\n\n" +
                                "Please try again or type /stop to cancell the registration.");
            }
        }

        private async Task ProceedNeedChannelSingleDevice(long userId, long chatId, Message message, ChatStateWithData state)
        {
            var text = message.Text?.Trim();
            bool? isSingleDevice = null;
            if (string.Equals(text, "single", StringComparison.OrdinalIgnoreCase))
            {
                isSingleDevice = true;
            }
            else if (string.Equals(text, "multiple", StringComparison.OrdinalIgnoreCase))
            {
                isSingleDevice = false;
            }
            else
            {
                await botClient.SendMessage(chatId,
                    "Please reply with *single* or *multiple*, or type /stop to cancel.",
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
                return;
            }

            await ProcessChannelForAdd(userId, chatId, state.ChannelName, state.ChannelKey, Convert.ToBase64String(state.ChannelKey), isSingleDevice);
        }

        private async Task ProcessChannelForAdd(long userId, long chatId, string channelName, byte[] channelKey, string channelKeyBase64, bool? isSingleDevice)
        {
            var dbChannel = await registrationService.FindChannelAsync(channelName, channelKey);
            if (dbChannel != null && await registrationService.HasChannelRegistrationAsync(chatId, dbChannel.Id))
            {
                await botClient.SendMessage(chatId, $"Channel {channelName} with same key is already registered in this chat. Registration aborted.");
                registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            // If channel doesn't exist yet and isSingleDevice not yet decided, ask
            if (dbChannel == null && !isSingleDevice.HasValue)
            {
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedSingleDevice,
                    ChannelName = channelName,
                    ChannelKey = channelKey,
                    IsSingleDevice = null
                });

                await botClient.SendMessage(chatId,
                    "Is this channel used by a single device only?\n\n" +
                    "• Reply *single* — Optimized Next-Hop routing will be used (works only for single device channels)\n" +
                    "• Reply *multiple* — Standard broadcast routing will be used\n\n" +
                    "Type /stop to cancel.",
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
                return;
            }

            var codesSent = registrationService.IncrementChannelCodesSentRecently(channelName, channelKeyBase64);
            if (codesSent > RegistrationService.MaxCodeVerificationTries)
            {
                await botClient.SendMessage(chatId, $"Channel {channelName} has reached the maximum number of verification codes sent. Please wait at least 1 hour before trying again to add the same channel to any chats. Registration aborted.");
                registrationService.SetChatState(userId, chatId, Models.ChatState.Default);
                return;
            }

            var code = RegistrationService.GenerateRandomCode();
            registrationService.StoreChannelPendingCodeAsync(userId, chatId, channelName, channelKey, isSingleDevice, code, DateTimeOffset.UtcNow.AddMinutes(5));

            var msg = await botClient.SendMessage(chatId,
                $"Verification code sent to channel {channelName}. Please reply with the received code here. The code is valid for 5 minutes.");

            var channel = new ChannelKey
            {
                ChannelXor = MeshtasticService.GenerateChannelHash(channelName, channelKey),
                PreSharedKey = channelKey,
                IsSingleDevice = false
            };

            await SendAndTrackMeshtasticMessage(
                channel,
                chatId,
                msg.MessageId,
                $"TMesh verification code is: {code}");

            registrationService.SetChatState(userId, chatId, Models.ChatState.AddingDevice_NeedCode);
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

            if (await registrationService.HasDeviceRegistrationAsync(chatId, deviceId))
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
            registrationService.StoreDevicePendingCodeAsync(userId, chatId, deviceId, code, DateTimeOffset.UtcNow.AddMinutes(5));

            var msg = await botClient.SendMessage(chatId,
                $"Verification code sent to device {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(deviceId)}). Please reply with the received code here. The code is valid for 5 minutes.");

            await SendAndTrackMeshtasticMessage(
                device,
                chatId,
                msg.MessageId,
                $"TMesh verification code is: {code}");

            registrationService.SetChatState(userId, chatId, Models.ChatState.AddingDevice_NeedCode);
        }

        private string ExtractFirstArgFromCommand(string commandText, string command)
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

            // Return null if empty (no argument provided)
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // Extract first word (argument should not have spaces)
            var parts = text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : null;
        }

        private (string name, string key, string mode) ExtractChannelFromCommand(string commandText, string command)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return default;
            }

            // Remove the command part and trim
            var text = commandText[command.Length..].Trim();
            if (text == $"@{_options.TelegramBotUserName}")
            {
                return default;
            }

            // Return null if empty (no device ID provided)
            if (string.IsNullOrWhiteSpace(text))
            {
                return default;
            }

            // Extract parts
            var parts = text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return default;
            }
            else if (parts.Length == 1)
            {
                return (parts[0], null, null);
            }
            else if (parts.Length == 2)
            {
                return (parts[0], parts[1], null);
            }
            else
            {
                return (parts[0], parts[1], parts[2]);
            }
        }


        private Task SendAndTrackMeshtasticMessage(
            IRecipient recipient,
            long chatId,
            int messageId,
            string text)
        {
            return SendAndTrackMeshtasticMessages(
                [recipient],
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
            IEnumerable<IRecipient> recipients,
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
                EstimatedSendDate = EstimateSendDelay(recipients.Count())
            };

            var messages = new List<(IRecipient recipient, long messageId, DeviceAndGatewayId)>();
            foreach (var recipient in recipients)
            {
                var newMeshMessageId = MeshtasticService.GetNextMeshtasticMessageId();
                var recStatus = new DeliveryStatusWithRecipientId
                {
                    RecipientId = recipient.RecipientDeviceId ?? recipient.RecipientChannelId,
                    Type = recipient.RecipientType,
                    Status = DeliveryStatus.Queued
                };
                DeviceAndGatewayId deviceAndGatewayId = null;
                if (recStatus.Type == RecipientType.ChannelSingle)
                {
                    deviceAndGatewayId = GetSingleDeviceChannelGateway((int)recipient.RecipientChannelId.Value);
                    if (deviceAndGatewayId == null)
                    {
                        recStatus.Type = RecipientType.ChannelMulti;
                    }
                }
                else if (recStatus.Type == RecipientType.Device)
                {
                    deviceAndGatewayId = GetDeviceGateway(recStatus.RecipientId.Value);
                }
                status.MeshMessages.Add(newMeshMessageId, recStatus);
                StoreMeshMessageStatus(newMeshMessageId, status);
                messages.Add((recipient, newMeshMessageId, deviceAndGatewayId));
            }

            StoreTelegramMessageStatus(
                chatId,
                messageId,
                status,
                trackForStatusResolve: recipients.Any(x => x.RecipientDeviceId.HasValue || x.IsSingleDeviceChannel == true));

            await ReportStatus(status);

            var replyToStatus = replyToTelegramMsgId.HasValue
                ? GetTelegramMessageStatus(chatId, replyToTelegramMsgId.Value)
                : null;

            foreach (var (recipient, newMeshMessageId, deviceAndGatewayId) in messages)
            {
                long? replyToMeshMessageId = null;

                var replyMsg = replyToStatus?.MeshMessages
                    .FirstOrDefault(kv =>
                        kv.Value.RecipientId != null &&
                        kv.Value.RecipientId == recipient.RecipientId &&
                        (kv.Value.Type == RecipientType.Device) == (recipient.RecipientType == RecipientType.Device));

                if (replyMsg != null && replyMsg.Value.Key != default)
                {
                    replyToMeshMessageId = replyMsg.Value.Key;
                }

                if (recipient.RecipientDeviceId != null)
                {
                    meshtasticService.SendDirectTextMessage(
                            newMeshMessageId,
                            recipient.RecipientDeviceId.Value,
                            recipient.RecipientKey,
                            text,
                            replyToMeshMessageId,
                            deviceAndGatewayId?.GatewayId,
                            hopLimit: deviceAndGatewayId?.ReplyHopLimit ?? int.MaxValue);
                }
                else
                {
                    meshtasticService.SendPrivateChannelTextMessage(
                            newMeshMessageId,
                            text,
                            replyToMeshMessageId,
                            relayGatewayId: deviceAndGatewayId?.GatewayId,
                            hopLimit: deviceAndGatewayId?.ReplyHopLimit ?? int.MaxValue,
                            new ChannelInternalInfo
                            {
                                Psk = recipient.RecipientKey,
                                Hash = recipient.RecipientChannelXor.Value
                            });
                }
            }
        }

        private async Task ProceedNeedCode(long userId, long chatId, Message message)
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
            if (message.Text?.StartsWith("/add_device", StringComparison.OrdinalIgnoreCase) == true)
            {
                var deviceIdFromCommand = ExtractFirstArgFromCommand(message.Text, "/add_device");
                await StartAddDevice(userId, chatId, deviceIdFromCommand);
                return;
            }
            if (message.Text?.StartsWith("/add_channel", StringComparison.OrdinalIgnoreCase) == true)
            {
                var (name, key, mode) = ExtractChannelFromCommand(message.Text, "/add_channel");
                await StartAddChannel(userId, chatId, name, key, mode);
                return;
            }
            if (message.Text?.StartsWith("/remove_device", StringComparison.OrdinalIgnoreCase) == true)
            {
                bool isRemoveFromAll = message.Text.StartsWith("/remove_device_from_all_chats", StringComparison.OrdinalIgnoreCase);
                var deviceIdFromCommand = ExtractFirstArgFromCommand(message.Text, isRemoveFromAll ? "/remove_device_from_all_chats" : "/remove_device");
                await StartRemoveDevice(userId, chatId, deviceIdFromCommand, isRemoveFromAll);
                return;
            }
            if (message.Text?.StartsWith("/remove_channel", StringComparison.OrdinalIgnoreCase) == true)
            {
                bool isRemoveFromAll = message.Text.StartsWith("/remove_channel_from_all_chats", StringComparison.OrdinalIgnoreCase);
                var channelIdFromCommand = ExtractFirstArgFromCommand(message.Text, isRemoveFromAll ? "/remove_channel_from_all_chats" : "/remove_channel");
                await StartRemoveChannel(userId, chatId, userId, channelIdFromCommand, isRemoveFromAll);
                return;
            }
            if (message.Text?.StartsWith("/status", StringComparison.OrdinalIgnoreCase) == true)
            {
                await HandleStatus(chatId, message.Text);
                return;
            }
            if (message.Text?.StartsWith("/position", StringComparison.OrdinalIgnoreCase) == true)
            {
                await HandlePosition(chatId, message.Text);
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
            var chatStateWithData = registrationService.GetChatState(userId, chatId);
            var chatState = chatStateWithData?.State;

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
                        meshtasticService.SendPublicTextMessage(
                            announcement,
                            relayGatewayId: null,
                            hopLimit: int.MaxValue,
                            publicChannelName: channelName);

                        await botClient.SendMessage(chatId, $"Announcement sent to {channelName}.");
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

                case "remove_node":
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

                        await registrationService.DeleteDeviceAsync(device.DeviceId);

                        await botClient.SendMessage(chatId, $"Deleted device {device.NodeName} ({device.DeviceId})");
                        return true;
                    }

                case "add_gateway":
                    {
                        var nodeId = segments.Length >= 2 ? segments[1] : string.Empty;

                        if (string.IsNullOrWhiteSpace(nodeId)
                           || !MeshtasticService.TryParseDeviceId(nodeId, out var parsedNodeId))
                        {
                            await botClient.SendMessage(chatId, $"Invalid node ID format: '{nodeId}'. The node ID can be decimal or hex (hex starts with ! or #).");
                            return true;
                        }

                        var hexId = MeshtasticService.GetMeshtasticNodeHexId(parsedNodeId);
                        var device = await registrationService.GetDeviceAsync(parsedNodeId);
                        var pwd = DeriveMqttPasswordForDevice(parsedNodeId);

                        await registrationService.RegisterGatewayAsync(parsedNodeId);
                        await botClient.SendMessage(chatId, $"Added gateway {device?.NodeName ?? hexId}.\r\nMQTT username: {hexId}\r\nMQTT password: {pwd}\r\n\r\nPassword only works with TMesh device firmware.");
                        GatewayListChanged = true;

                        return true;
                    }
                case "remove_gateway":
                    {
                        var nodeId = segments.Length >= 2 ? segments[1] : string.Empty;

                        if (string.IsNullOrWhiteSpace(nodeId)
                           || !MeshtasticService.TryParseDeviceId(nodeId, out var parsedNodeId))
                        {
                            await botClient.SendMessage(chatId, $"Invalid node ID format: '{nodeId}'. The node ID can be decimal or hex (hex starts with ! or #).");
                            return true;
                        }
                        var hexId = MeshtasticService.GetMeshtasticNodeHexId(parsedNodeId);
                        var device = await registrationService.GetDeviceAsync(parsedNodeId);
                        var removed = await registrationService.UnregisterGatewayAsync(parsedNodeId);
                        if (removed)
                        {
                            await botClient.SendMessage(chatId, $"Removed gateway {device?.NodeName ?? hexId}.");
                        }
                        else
                        {
                            await botClient.SendMessage(chatId, $"Gateway {device?.NodeName ?? hexId} was not registered.");
                        }

                        GatewayListChanged = GatewayListChanged || removed;
                        return true;
                    }
                case "list_gateways":
                    {
                        var ids = await registrationService.GetGatewaysCached();
                        var sb = new StringBuilder();
                        sb.AppendLine("Registered gateways:");
                        foreach (var id in ids)
                        {
                            var device = await registrationService.GetDeviceAsync(id);
                            var hexId = MeshtasticService.GetMeshtasticNodeHexId(id);
                            sb.AppendLine($"• {device?.NodeName ?? hexId} ({hexId})");
                        }
                        sb.AppendLine();
                        sb.AppendLine("Default gateways:");
                        foreach (var id in _options.GatewayNodeIds)
                        {
                            var device = await registrationService.GetDeviceAsync(id);
                            var hexId = MeshtasticService.GetMeshtasticNodeHexId(id);
                            sb.AppendLine($"• {device?.NodeName ?? hexId} ({hexId})");
                        }
                        await botClient.SendMessage(chatId, sb.ToString());
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

                        //idented
                        var json = JsonSerializer.Serialize(device, IdentedOptions);

                        var registrations = await registrationService.GetChatsByDeviceIdCached(device.DeviceId);


                        await botClient.SendMessage(
                            chatId,
                            $"Found node:\r\n\r\n" +
                            json + "\r\n\r\nRegistrations: " + registrations.Count);

                        return true;
                    }

                default:
                    {
                        await botClient.SendMessage(chatId, $"Unknown admin command: {command}");
                        return true;
                    }
            }
        }

        private string DeriveMqttPasswordForDevice(long deviceId)
        {
            var username = MeshtasticService.GetMeshtasticNodeHexId(deviceId);
            var secret = _options.DefaultMqttPasswordDeriveSecret;
            if (_options.MqttUserPasswordDeriveSecrets != null
                && _options.MqttUserPasswordDeriveSecrets.TryGetValue(username, out var sec))
            {
                secret = sec;
            }

            return MqttPasswordDerive.DerivePassword(username, secret);
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

            var channelRegs = await registrationService.GetChannelKeysByChatIdCached(chatId);
            var devRegs = await registrationService.GetDeviceKeysByChatIdCached(chatId);
            if (devRegs.Count == 0
                && channelRegs.Count == 0)
            {
                await botClient.SendMessage(
                    chatId,
                    NoDeviceOrChannelMessage,
                    replyParameters: new ReplyParameters
                    {
                        AllowSendingWithoutReply = false,
                        ChatId = chatId,
                        MessageId = msgId,
                    });
                return;
            }

            await SendAndTrackMeshtasticMessages(
                channelRegs.AsEnumerable<IRecipient>().Concat(devRegs),
                chatId,
                msgId,
                replyToTelegramMessageId,
                textToMesh);
        }

        private async Task HandleStatus(long chatId, string cmdText)
        {
            var channelRegs = await registrationService.GetChannelNamesByChatId(chatId);
            var devices = await registrationService.GetDeviceNamesByChatId(chatId);
            if (devices.Count == 0
                && channelRegs.Count == 0)
            {
                await botClient.SendMessage(chatId, NoDeviceOrChannelMessage);
            }
            else
            {
                var filter = GetCmdParam(cmdText);
                var now = DateTime.UtcNow;
                bool hasFilter = !string.IsNullOrEmpty(filter);

                if (hasFilter)
                {
                    devices = [.. devices.Where(d => d.NodeName.Contains(filter, StringComparison.OrdinalIgnoreCase))];
                    channelRegs = [.. channelRegs.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];
                }

                if (hasFilter
                    && devices.Count == 0
                    && channelRegs.Count == 0)
                {
                    await botClient.SendMessage(chatId, $"No registered devices or channels matching filter '{filter}'.");
                    return;
                }

                var lines =
                        channelRegs.Select(c => $"• Channel: {c.Name} (ID {c.Id}) {(c.IsSingleDevice ? " [Single Device]" : "")}")
                        .Concat(
                          devices.Select(d => $"• Device: {d.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId)}), last node info {FormatTimeSpan(now - d.LastNodeInfo)} ago, last position update {(d.LastPositionUpdate != null ? FormatTimeSpan(now - d.LastPositionUpdate.Value) + " ago" : "N/A")}")
                        );

                var text = $"{(hasFilter ? "Filtered" : "Registered")} channels and devices:\r\n" + string.Join("\r\n", lines);
                await botClient.SendMessage(chatId, text);
            }
        }

        private static string GetCmdParam(string cmdFullText)
        {
            var firstSpaceIndex = cmdFullText.IndexOf(' ');
            if (firstSpaceIndex == -1)
            {
                return null;
            }
            else
            {
                return cmdFullText[(firstSpaceIndex + 1)..].Trim();
            }
        }

        private async Task HandlePosition(long chatId, string cmdText)
        {
            var devices = await registrationService.GetDevicePositionByChatId(chatId);
            if (devices.Count == 0)
            {
                await botClient.SendMessage(chatId, NoDeviceMessage);
            }
            else
            {
                var filter = GetCmdParam(cmdText);
                var now = DateTime.UtcNow;
                bool hasFilter = !string.IsNullOrEmpty(filter);

                if (hasFilter)
                {
                    devices = [.. devices.Where(d => d.NodeName.Contains(filter, StringComparison.OrdinalIgnoreCase))];
                }

                if (hasFilter && devices.Count == 0)
                {
                    await botClient.SendMessage(chatId, $"No registered devices matching filter '{filter}'.");
                    return;
                }

                var unknownPositionMsg = new StringBuilder();
                foreach (var d in devices)
                {
                    if (d.LastPositionUpdate == null)
                    {
                        unknownPositionMsg.AppendLine($"• Device: {d.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId)})");
                    }
                    else
                    {
                        await botClient.SendMessage(
                            chatId,
                            $"Device: {d.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId)}), last position update {FormatTimeSpan(now - d.LastPositionUpdate.Value)} ago:");

                        await botClient.SendLocation(
                            chatId,
                            d.Latitude.Value,
                            d.Longitude.Value,
                            horizontalAccuracy: d.AccuracyMeters.HasValue ? Math.Min(d.AccuracyMeters.Value, 1500) : null,
                            replyParameters: null);
                    }
                }

                if (unknownPositionMsg.Length > 0)
                {
                    await botClient.SendMessage(
                        chatId,
                        "Devices without known position:\r\n" +
                        unknownPositionMsg.ToString());
                }
            }
        }

        static string FormatTimeSpan(TimeSpan ts)
        {
            return ts.Days > 0
                ? ts.ToString(@"d\:hh\:mm\:ss")
                : ts.ToString(@"hh\:mm\:ss");
        }

        private async Task StartAddChannel(long userId, long chatId, string channelNameText, string channelKey, string mode = null)
        {
            if (string.IsNullOrWhiteSpace(channelNameText))
            {
                // No channel name provided, ask for it
                await botClient.SendMessage(chatId, "Please send your Meshtastic channel name.");
                registrationService.SetChatState(userId, chatId, ChatState.AddingChannel_NeedName);
                return;
            }

            // Channel name provided in command, validate it
            if (!MeshtasticService.IsValidChannelName(channelNameText))
            {
                await botClient.SendMessage(chatId,
                     $"Invalid channel name format: '{channelNameText}'. The channel name must be a valid Meshtastic channel name (less than 12 bytes).\n\n" +
                     "Examples:\n" +
                     "• /add_channel MyChannel ZGeGFyhk<...>3sUOUGyaHqrvU= single\n" +
                     "• /add_channel MyChannel ZGeGFyhk<...>3sUOUGyaHqrvU= multiple\n" +
                     "Or use /add_channel without parameters and I'll ask for the channel name and key.");
                return;
            }

            if (string.IsNullOrEmpty(channelKey))
            {
                await SendNeedChannelKeyTgMsg(chatId);
                registrationService.SetChatStateWithData(userId, chatId, new ChatStateWithData
                {
                    State = ChatState.AddingChannel_NeedKey,
                    ChannelName = channelNameText
                });
                return;
            }

            if (!MeshtasticService.TryParseChannelKey(channelKey, out var keyBytes))
            {
                await botClient.SendMessage(chatId,
                             $"Invalid channel key format: '{channelKey}'. The channel key must be a valid Meshtastic channel key (base64-encoded, 16 or 32 bytes).\n\n" +
                             "Examples:\n" +
                             "• /add_channel MyChannel ZGeGFyhk<...>3sUOUGyaHqrvU= single\n" +
                             "Or use /add_channel without parameters and I'll ask for the channel name and key.");
                return;
            }

            if (meshtasticService.IsPublicChannel(channelNameText, keyBytes))
            {
                await botClient.SendMessage(chatId,
                             $"Adding public, well known channels is not allowed.");
                return;
            }

            // Parse optional mode argument: single / multiple
            bool? isSingleDevice = null;
            if (!string.IsNullOrWhiteSpace(mode))
            {
                if (string.Equals(mode, "single", StringComparison.OrdinalIgnoreCase))
                    isSingleDevice = true;
                else if (string.Equals(mode, "multiple", StringComparison.OrdinalIgnoreCase))
                    isSingleDevice = false;
                else
                {
                    await botClient.SendMessage(chatId,
                        $"Invalid mode '{mode}'. Use 'single' or 'multiple' as the third argument.");
                    return;
                }
            }

            await ProcessChannelForAdd(userId, chatId, channelNameText, keyBytes, channelKey, isSingleDevice);
        }







        private async Task SendNeedChannelKeyTgMsg(long chatId)
        {
            await botClient.SendMessage(chatId, "Please send your Meshtastic channel key (base64-encoded, 16 or 32 bytes).");
        }



        private async Task StartAddDevice(long userId, long chatId, string deviceIdText)
        {
            if (string.IsNullOrWhiteSpace(deviceIdText))
            {
                // No device ID provided, ask for it
                await botClient.SendMessage(chatId, "Please send your Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #).");
                registrationService.SetChatState(userId, chatId, Models.ChatState.AddingDevice_NeedId);
                return;
            }

            // Device ID provided in command, process immediately
            if (!MeshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await botClient.SendMessage(chatId,
                    $"Invalid device ID format: '{deviceIdText}'. The device ID can be decimal or hex (hex starts with ! or #).\n\n" +
                    "Examples:\n" +
                    "• /add_device 123456789\n" +
                    "• /add_device !75bcd15\n" +
                    "• /add_device #75bcd15\n\n" +
                    "Or use /add_device without parameters and I'll ask for the device ID.");
                return;
            }

            // Process the device ID (same logic as ProcessNeedDeviceId)
            await ProcessDeviceIdForAdd(userId, chatId, deviceId);
        }

        private async Task StartRemoveDevice(long userId, long chatId, string deviceIdText, bool isRemoveFromAll)
        {
            if (string.IsNullOrWhiteSpace(deviceIdText))
            {
                // No device ID provided, ask for it
                await botClient.SendMessage(chatId, "Please send your Meshtastic device ID. The device ID can be decimal or hex (hex starts with ! or #).");
                registrationService.SetChatState(userId, chatId,
                    isRemoveFromAll ? Models.ChatState.RemovingDeviceFromAll : Models.ChatState.RemovingDevice);
                return;
            }

            // Device ID provided in command, process immediately
            if (!MeshtasticService.TryParseDeviceId(deviceIdText, out var deviceId))
            {
                await botClient.SendMessage(chatId,
                    $"Invalid device ID format: '{deviceIdText}'. The device ID can be decimal or hex (hex starts with ! or #).\n\n" +
                    "Examples:\n" +
                    "• /remove_device 123456789\n" +
                    "• /remove_device !75bcd15\n" +
                    "• /remove_device #75bcd15\n\n" +
                    "Or use /remove_device without parameters and I'll ask for the device ID.");
                return;
            }

            await ExecuteRemoveDevice(chatId, deviceId, isRemoveFromAll);
        }


        private async Task StartRemoveChannel(long userId, long chatId, long telegramUserId, string channelIdText, bool isRemoveFromAll)
        {
            if (string.IsNullOrWhiteSpace(channelIdText))
            {
                var channelRegs = await registrationService.GetChannelNamesByChatId(chatId);
                if (channelRegs.Count == 0)
                {
                    await botClient.SendMessage(chatId, "No channels are registered in this chat.");
                    return;
                }

                var lines = channelRegs.Select(c => $"• {c.Name} (ID {c.Id})");

                var sb = new StringBuilder("Please send the ID of the channel you want to remove.");
                sb.AppendLine();
                sb.AppendLine("Registered channels:");
                lines.ToList().ForEach(l => sb.AppendLine(l));

                // No channel ID provided, ask for it
                await botClient.SendMessage(chatId, sb.ToString());
                registrationService.SetChatState(userId, chatId,
                    isRemoveFromAll ? Models.ChatState.RemovingChannelFromAll : Models.ChatState.RemovingChannel);
                return;
            }

            // Channel ID provided in command, process immediately
            if (!int.TryParse(channelIdText, out var channelId))
            {
                await botClient.SendMessage(chatId,
                    $"Invalid channel ID format: '{channelIdText}'. The channel ID must be a valid integer.\n\nPlease use remove command without params to see ids of registered channels\r\n" +
                    "Examples:\n" +
                    "• /remove_channel 123456789\n" +
                    "Or use /remove_channel without parameters and I'll ask for the channel ID.");
                return;
            }

            await ExecuteRemoveChannel(chatId, telegramUserId, channelId, isRemoveFromAll);
        }

        private async Task ExecuteRemoveDevice(long chatId, long deviceId, bool isRemoveFromAll)
        {
            if (isRemoveFromAll)
            {
                // Process removal from all chats
                var removedFromAll = await registrationService.RemoveDeviceFromAllChatsViaOneChatAsync(chatId, deviceId);
                if (!removedFromAll)
                {
                    await botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} is not registered in this chat. To prove ownership of device please register it first in this chat then retry the command.");
                }
                else
                {
                    await botClient.SendMessage(chatId, $"Device {MeshtasticService.GetMeshtasticNodeHexId(deviceId)} has been removed from all chats.");
                }
            }
            else
            {
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
        }


        private async Task ExecuteRemoveChannel(long chatId, long telegramUserId, int channelId, bool isRemoveFromAll)
        {
            if (isRemoveFromAll)
            {
                // Process removal from all chats
                var (removedFromCurrentChat, removedFromOtherChats) = await registrationService.RemoveChannelFromAllChatsViaOneChatAsync(chatId, telegramUserId, channelId);
                if (!removedFromCurrentChat)
                {
                    await botClient.SendMessage(chatId, $"Channel {channelId} is not registered in this chat. To prove that you still have access to the channel please register it first in this chat then retry the command.");
                }
                else if (removedFromOtherChats)
                {
                    await botClient.SendMessage(chatId, $"Channel {channelId} has been removed from this chat and all other chats where you have registered it. Registration of the channel created by other Telegram users in other chats are not removed.");
                }
                else
                {
                    await botClient.SendMessage(chatId, $"Channel {channelId} has been removed from this chat.");
                }
            }
            else
            {
                // Process removal immediately
                var removed = await registrationService.RemoveChannelFromChat(chatId, channelId);
                if (!removed)
                {
                    await botClient.SendMessage(chatId, $"Channel {channelId} is not registered in this chat.");
                }
                else
                {
                    await botClient.SendMessage(chatId, $"Channel {channelId} has been removed from this chat.");
                }
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
            if (message.ChannelId.HasValue && message.IsSingleDeviceChannel)
            {
                StoreChannelGateway((int)message.ChannelId.Value, message.GatewayId, message.DeviceId, message.GetSuggestedReplyHopLimit());
            }
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
                case MeshMessageType.DeviceMetrics:
                    await ProcessInboundDeviceMetricsMessage((DeviceMetricsMessage)message, deviceOrNull);
                    break;
                case MeshMessageType.AckMessage:
                default:
                    logger.LogWarning("Received unsupported Meshtastic message type: {MessageType}", message.MessageType);
                    break;
            }
        }

        private async Task ProcessInboundDeviceMetricsMessage(DeviceMetricsMessage message, Device deviceOrNull)
        {
            if (message.NeedAck)
            {
                meshtasticService.AckMeshtasticMessage(deviceOrNull.PublicKey, message, message.GatewayId);
            }
            var analyticsService = services.GetService<AnalyticsService>();
            if (analyticsService == null)
            {
                return;
            }

            deviceOrNull ??= await registrationService.GetDeviceAsync(message.DeviceId);
            if (deviceOrNull?.LocationUpdatedUtc == null)
            {
                return;
            }
            logger.LogDebug("Processing inbound Meshtastic message: {Message}", message);

            var metrics = new DeviceMetric
            {
                DeviceId = (uint)message.DeviceId,
                Timestamp = Instant.FromDateTimeUtc(DateTime.UtcNow),
                Latitude = deviceOrNull.Latitude ?? 0,
                Longitude = deviceOrNull.Longitude ?? 0,
                LocationUpdatedUtc = Instant.FromDateTimeUtc(DateTime.SpecifyKind(deviceOrNull.LocationUpdatedUtc.Value, DateTimeKind.Utc)),
                AccuracyMeters = deviceOrNull.AccuracyMeters,
                ChannelUtil = message.ChannelUtilization,
                AirUtil = message.AirUtilization,
            };

            await analyticsService.RecordEventAsync(metrics);
        }


        private async Task ProcessInboundPositionMessage(PositionMessage message, Device deviceOrNull)
        {
            if (message.NeedAck)
            {
                meshtasticService.AckMeshtasticMessage(deviceOrNull?.PublicKey, message, message.GatewayId);
            }
            deviceOrNull ??= await registrationService.GetDeviceAsync(message.DeviceId);
            if (deviceOrNull == null)
            {
                return;
            }
            logger.LogDebug("Processing inbound Meshtastic message: {Message}", message);
            deviceOrNull.LocationUpdatedUtc = DateTime.UtcNow;
            deviceOrNull.Longitude = message.Longitude;
            deviceOrNull.Latitude = message.Latitude;
            deviceOrNull.AccuracyMeters = (int)Math.Round(message.AccuracyMeters);
            await registrationService.SaveAssumeChanged(deviceOrNull);
            if (!message.SentToOurNodeId)
            {
                return;
            }

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
            else if (message.ChannelId != null)
            {
                await ProcessInboundPrivateChannelMeshTextMessage(message, deviceOrNull);
            }
            else
            {
                await ProcessInboundPublicMeshTextMessage(message, deviceOrNull);
            }

        }

        private async Task ProcessInboundPublicMeshTextMessage(TextMessage message, Device deviceOrNull)
        {
            if (!_options.ReplyToPublicPingsViaDirectMessage
                && _options.PingWords.Length == 0)
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

        private async Task ProcessInboundPrivateChannelMeshTextMessage(TextMessage message, Device deviceOrNull)
        {
            var channel = await registrationService.GetChannelAsync((int)message.ChannelId.Value);
            if (channel == null)
            {
                //how we have decrypted channel ID but can't find it in the database
                return;
            }

            deviceOrNull ??= await registrationService.GetDeviceAsync(message.DeviceId);

            var deviceName = deviceOrNull != null ? deviceOrNull.NodeName : MeshtasticService.GetMeshtasticNodeHexId(message.DeviceId);

            string cmdText = null;
            if (message.Text != null
                && message.Text.Trim().Length > 1
                && message.Text.StartsWith('/'))
            {
                cmdText = message.Text[1..].Trim();
            }

            if (cmdText != null &&
                _options.PingWords.Any(pingWord => string.Equals(cmdText, pingWord, StringComparison.OrdinalIgnoreCase)))
            {
                meshtasticService.SendPrivateChannelTextMessage(
                    MeshtasticService.GetNextMeshtasticMessageId(),
                    _options.Texts.PingReply ?? "pong",
                    replyToMessageId: message.Id,
                    relayGatewayId: message.GatewayId,
                    hopLimit: message.GetSuggestedReplyHopLimit(),
                    channel);

                meshtasticService.AddStat(new Shared.Models.MeshStat
                {
                    PongSent = 1,
                });
                return;
            }
            var chatIds = await registrationService.GetChatsByChannelIdCached(channel.Id);
            if (chatIds.Count == 0)
            {
                //race condition, no regitration for channel, ignore message
                return;
            }


            var text = message.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("Received empty text message from channel {ChannelId}", channel.Id);
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
                        $"{deviceName} [#{channel.Name}]: {text}",
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
                        MeshMessages = new Dictionary<long, DeliveryStatusWithRecipientId>
                            {
                                { message.Id, new DeliveryStatusWithRecipientId
                                    {
                                        RecipientId = channel.Id,
                                        Type = channel.IsSingleDevice
                                            ? RecipientType.ChannelSingle
                                            : RecipientType.ChannelMulti,
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
                        $"{deviceName} [#{channel.Name}]: {text}");

                    var status = new MeshtasticMessageStatus
                    {
                        TelegramChatId = chatId,
                        TelegramMessageId = msg.Id,
                        MeshMessages = new Dictionary<long, DeliveryStatusWithRecipientId>
                            {
                                { message.Id, new DeliveryStatusWithRecipientId
                                    {
                                        RecipientId = channel.Id,
                                        Type = channel.IsSingleDevice ? RecipientType.ChannelSingle: RecipientType.ChannelMulti,
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
                cmdText = message.Text[1..].Trim();
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
                        MeshMessages = new Dictionary<long, DeliveryStatusWithRecipientId>
                            {
                                { message.Id, new DeliveryStatusWithRecipientId
                                    {
                                        RecipientId = message.DeviceId,
                                        Type = RecipientType.Device,
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
                        MeshMessages = new Dictionary<long, DeliveryStatusWithRecipientId>
                            {
                                { message.Id, new DeliveryStatusWithRecipientId
                                    {
                                        RecipientId = message.DeviceId,
                                        Type = RecipientType.Device,
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

            var updated = await registrationService.SaveDeviceAsync(
                message.DeviceId,
                message.NodeName,
                message.PublicKey);

            if (!updated)
            {
                var device = await registrationService.GetDeviceAsync(message.DeviceId);
                if (device == null)
                {
                    return;
                }
                //Node public key do not match our records, delivery of messages to this node will not work
                //we need to warn users that his public key was changed and he need to remove device and readded it
                var chatIds = await registrationService.GetChatsByDeviceIdCached(message.DeviceId);
                foreach (var chatId in chatIds)
                {
                    await botClient.SendMessage(
                        chatId,
                        $"Warning: The new public key was detected for device {device.NodeName}. If you have recently reset your device or changed encryption keys, please remove the device (using /remove_device command) and add it back for messaging to work. Public keys are not updated automaticly after device first registration due security reasons. If you haven't changed the keys or reset your device please take it as a warning, some node in the network is using your device id.");
                }
            }
        }

        public async Task ProcessAckMessages(List<AckMessage> batch)
        {
            foreach (var item in batch)
            {
                if (item.ChannelId.HasValue && item.IsSingleDeviceChannel)
                {
                    StoreChannelGateway((int)item.ChannelId.Value, item.GatewayId, item.DeviceId, item.GetSuggestedReplyHopLimit());
                }
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
