using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TBot.Database;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
using TBot.Models.ChatSession;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TBot.Bot
{
    public class TgBotService(
        TelegramBotClient botClient,
        IOptions<TBotOptions> options,
        RegistrationService registrationService,
        MeshtasticService meshtasticService,
        BotCache botCache,
        ILogger<TgBotService> logger,
        IServiceProvider services,
        TBotDbContext db)
    {

        const int TrimUserNamesToLength = 8;
        public const string NoDeviceOrChannelMessage = "No registered devices or channels. You can register a new device with the /add_device command, channel with /add_channel command, or start a temporary chat with /chat !<deviceId>. Please remove the bot from the group if you don't need it.";
        public const string NoDeviceMessage = "No registered devices. You can register a new device with the /add_device command.";
        private readonly TBotOptions _options = options.Value;
        public static readonly JsonSerializerOptions IdentedOptions = new()
        {
            WriteIndented = true,
        };

        public HashSet<int> NetworkGatewayListChanged { get; private set; }
        public HashSet<int> NetworkPublicChannelsChanged { get; private set; }
        public bool NetworksUpdated { get; private set; }

        public List<MeshtasticMessageStatus> TrackedMessages => botMeshSender?.TrackedMessages;

        private MeshtasticBotMsgStatusTracker botMeshSender;

        public async Task InstallWebhook()
        {
            await botClient.SetWebhook(
                _options.TelegramUpdateWebhookUrl,
                allowedUpdates: [UpdateType.Message, UpdateType.MessageReaction],
                secretToken: _options.TelegramWebhookSecret);

            await botClient.SetMyCommands(
            [
                new BotCommand
                {
                    Command = "start",
                    Description = "Allows Meshtastic devices to send new chat request to this Telegram chat with /chat command."
                },
                new BotCommand {
                    Command = "disable",
                    Description = "Disables new chat requests from Meshtastic. When bot is disabled approved or registered devices still allowed to start chat sessions, only requests from unknown devices are blocked. Use /start to enable it again."
                },
                new BotCommand {
                    Command = "kill",
                    Description = "Remove all registration and approvals and disable new chat requests from Meshtastic devices and channels."
                },
                new BotCommand
                {
                    Command = "chat",
                    Description = $"Start a chat session with a Meshtastic device without registering it. Use device ID as parameter (e.g., /chat !aabbcc11). Chat session automaticly expires when no new messages are sent or when /end_chat command is used."
                },
                new BotCommand
                {
                    Command = "chat_channel",
                    Description = $"Start a chat session with a Meshtastic channel without registering it. e.g., /chat_channel MyChannel:123, 123 - is TMesh channel ID created on registration. Use /end_chat command to stop the chat session."
                },
                new BotCommand
                {
                    Command = "end_chat",
                    Description = "Stop active chat session started with /chat or /chat_channel command."
                },
                new BotCommand
                {
                    Command = "add_device",
                    Description = "Register a Meshtastic device (e.g., /add_device !aabbcc11)"
                },
                new BotCommand
                {
                    Command = "add_channel",
                    Description = "Register a Meshtastic private channel"
                },
                new BotCommand
                {
                    Command = "remove_device",
                    Description = "Unregister a Meshtastic device or remove it from approved devices (e.g., /remove_device !aabbcc11)"
                },
                new BotCommand
                {
                    Command = "remove_channel",
                    Description = "Unregister a Meshtastic private channel or remove it from approved channels (e.g., /remove_channel <ChannelID>). Get registered channel ID with /status command or run /remove_channel without params to see registered channel IDs."
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
                    Description = "Show status of current chat, list of registered and approved Meshtastic devices and channels, supports filter by name (e.g. /status MyDevice)"
                },
                new BotCommand
                {
                    Command = "position",
                    Description = "Show last known position of registered Meshtastic devices as map, supports filter by name (e.g. /position MyDevice)"
                },
                new BotCommand
                {
                    Command = "promote_to_gateway",
                    Description = "Promote a registered device to MQTT gateway. Requires custom TMesh firmware to be installed on the device. (e.g., /promote_to_gateway !aabbcc11)"
                },
                new BotCommand
                {
                    Command = "demote_from_gateway",
                    Description = "Remove a device from MQTT gateway role (e.g., /demote_from_gateway !aabbcc11)"
                },
                new BotCommand
                {
                    Command = "list_networks",
                    Description = "List all available networks and their public channels"
                }
            ]);
        }

        public async Task<WebhookInfo> CheckInstall()
        {
            return await botClient.GetWebhookInfo();
        }

        private async Task HandleTgUpdate(Update update)
        {
            switch (update.Type)
            {
                case UpdateType.Message:
                    await HandleTelegramMessageUpdate(update.Message);
                    break;
                case UpdateType.MessageReaction:
                    await HandleTgReactionUpdate(update.MessageReaction);
                    break;
            }
        }

        private async Task<bool> HandleTgReactionUpdate(MessageReactionUpdated msgReaction)
        {
            if (msgReaction.User == null
                || msgReaction.Chat == null
                //No reaction changes updates
                || msgReaction.OldReaction != null
                        && msgReaction.OldReaction.Length > 0
                || msgReaction.NewReaction == null
                || msgReaction.NewReaction.Length == 0)
            {
                return false;
            }

            if (msgReaction.User.IsBot
                && msgReaction.User.Username == _options.TelegramBotUserName)
            {
                // Ignore messages from the bot itself
                return false;
            }

            var chatId = msgReaction.Chat.Id;
            var msgId = msgReaction.MessageId;

            var emojis = string.Join(null,
                    msgReaction.NewReaction.Select(ConvertReactionType)
                );

            var userName = GetTelegramUserName(msgReaction.User);
            var trimmedUserName = TrimTelegramUserName(userName);

            var activeSession = botCache.GetActiveChatSession(chatId);
            if (activeSession != null)
            {
                var recipient = await GetChatSessionRecipient(activeSession);
                EnsureMeshSenderCreated().SendMeshtasticMessageReactions(
                   [recipient],
                   chatId,
                   msgId,
                   $"{trimmedUserName}{emojis}");

                return true;
            }
            var channelRegs = await registrationService.GetChannelKeysByChatIdCached(chatId);
            var devRegs = await registrationService.GetDeviceKeysByChatIdCached(chatId);
            if (devRegs.Count == 0
                && channelRegs.Count == 0)
            {
                return false;
            }

            EnsureMeshSenderCreated().SendMeshtasticMessageReactions(
                channelRegs.AsEnumerable<IRecipient>().Concat(devRegs),
                chatId,
                msgId,
                $"{trimmedUserName}{emojis}");

            return true;
        }

        private MeshtasticBotMsgStatusTracker EnsureMeshSenderCreated()
        {
            botMeshSender ??= services.GetRequiredService<MeshtasticBotMsgStatusTracker>();
            return botMeshSender;
        }

        private string ConvertReactionType(ReactionType reaction)
        {
            return reaction switch
            {
                ReactionTypeEmoji emojiReaction => emojiReaction.Emoji,
                ReactionTypeCustomEmoji => "?",
                ReactionTypePaid => "?",
                _ => "?"
            };
        }

        private async Task<bool> HandleTelegramMessageUpdate(Message msg)
        {
            if (msg == null
                || msg.Chat == null
                || msg.From == null) return false;

            if (msg.From.IsBot && msg.From.Username == _options.TelegramBotUserName)
            {
                // Ignore messages from the bot itself
                return false;
            }

            var chatId = msg.Chat.Id;
            var userId = msg.From.Id;

            var chatStateWithData = registrationService.GetChatState(userId, chatId);

            if (chatStateWithData != null
                && chatStateWithData.State != ChatState.Default
                && chatStateWithData.State != ChatState.Admin)
            {
                var res = await services.GetRequiredService<TgCommandBotService>()
                    .ProcessChatWithState(msg, chatStateWithData);
                if (res.Handled)
                {
                    await HandleNestedResult(res);
                    return true;
                }
            }

            await HandleDefaultUpdate(userId, chatId, msg);
            return true;
        }

        private async Task HandleDefaultUpdate(
           long userId,
           long chatId,
           Message msg)
        {
            if (msg.Text != null && msg.Text.StartsWith('/'))
            {
                var res = await services.GetRequiredService<TgCommandBotService>()
                    .HandleCommand(userId, chatId, msg);
                if (res.Handled)
                {
                    await HandleNestedResult(res);
                    return;
                }
            }

            var trimmedText = msg.Text?.Trim();
            if (trimmedText != null
                && trimmedText.Length == RegistrationService.CodeLength
                && trimmedText.All(Char.IsDigit))
            {
                var pendingRequest = botCache.GetPendingChatRequest_MeshToTg(chatId);
                if (pendingRequest != null && pendingRequest.Code == trimmedText)
                {
                    await ApproveChatRequestFromMesh(chatId, msg.From?.GetUserNameOrName(), pendingRequest);
                    return;
                }
            }

            if (msg.Text != null)
            {
                string userName = GetTelegramUserName(msg.From);
                await HandleText(
                    msg.MessageId,
                    userName,
                    chatId,
                    msg.ReplyToMessage?.MessageId,
                    msg.Text);
            }
        }

        private async Task ApproveChatRequestFromMesh(long chatId, string username, DeviceOrChannelRequestCode request)
        {
            var otherMeshSession = botCache.GetActiveChatSession(chatId);
            if (otherMeshSession != null
                && (otherMeshSession.DeviceId != request.DeviceId
                || otherMeshSession.ChannelId != request.ChannelId))
            {
                var tgChat = await registrationService.GetTgChatByChatIdAsync(chatId);
                var chatName = tgChat != null ? tgChat.ChatName : $"@{username}";

                await botCache.StopChatSession(chatId, db);
                IRecipient recipient = otherMeshSession.DeviceId != null
                    ? await registrationService.GetDeviceAsync(otherMeshSession.DeviceId.Value)
                    : await registrationService.GetChannelAsync(otherMeshSession.ChannelId.Value);

                if (recipient != null)
                {
                    var gatewayId = botCache.GetRecipientGateway(recipient);

                    meshtasticService.SendTextMessage(
                        recipient,
                        $"❌ Chat with {chatName} is ended",
                        replyToMessageId: null,
                        relayGatewayId: gatewayId,
                        hopLimit: int.MaxValue);
                }
            }

            var otherTgChatId = botCache.GetActiveChatSessionForRequest(request);
            if (otherTgChatId != null && otherTgChatId != chatId)
            {
                IRecipient recipient = request.DeviceId != null
                    ? await registrationService.GetDeviceAsync(request.DeviceId.Value)
                    : await registrationService.GetChannelAsync(request.ChannelId.Value);
                await botCache.StopChatSession(otherTgChatId.Value, db);
                var recipientName = recipient != null ? await registrationService.GetRecipientName(recipient) : "Unknown";
                await botClient.TrySendMessage(
                    registrationService,
                    logger,
                    otherTgChatId.Value,
                    $"❌ Chat with {recipientName} is ended by device");
            }

            if (request.DeviceId != null)
            {
                var device = await registrationService.GetDeviceAsync(request.DeviceId.Value);
                if (device == null)
                {
                    await botClient.TrySendMessage(
                        registrationService,
                        logger,
                        chatId,
                        $"Device with ID {request.DeviceId.Value} not found. Cannot approve chat request.");
                    return;
                }

                await botCache.StartChatSession(chatId, new DeviceOrChannelId
                {
                    DeviceId = request.DeviceId,
                    ChannelId = null
                }, db);

                botCache.RemovePendingChatRequest_MeshToTg(chatId);

                var tgChat = await registrationService.GetTgChatByChatIdAsync(chatId);
                if (tgChat != null && tgChat.IsActive)
                {
                    await registrationService.ApproveDeviceForChatAsync(tgChat.ChatId, request.DeviceId.Value);
                }

                await botClient.SendMessage(chatId,
                    $"✅ You started chat with {device.NodeName} ({MeshtasticService.GetMeshtasticNodeHexId(device.DeviceId)}). Chat is now active.");

                meshtasticService.SendDirectTextMessage(
                      device.DeviceId,
                      device.NetworkId,
                      device.PublicKey,
                      "✅ Chat request approved. You can send messages.",
                      replyToMessageId: null,
                      relayGatewayId: null,
                      hopLimit: int.MaxValue);
            }
            else
            {
                var channel = await registrationService.GetChannelAsync(request.ChannelId.Value);
                if (channel == null)
                {
                    await botClient.TrySendMessage(
                        registrationService,
                        logger,
                        chatId,
                        $"Channel with ID {request.ChannelId.Value} not found. Cannot approve chat request.");
                    return;
                }
                await botCache.StartChatSession(chatId, new DeviceOrChannelId
                {
                    DeviceId = null,
                    ChannelId = request.ChannelId
                }, db);
                botCache.RemovePendingChatRequest_MeshToTg(chatId);
                var tgChat = await registrationService.GetTgChatByChatIdAsync(chatId);
                if (tgChat != null && tgChat.IsActive)
                {
                    await registrationService.ApproveChannelForChatAsync(chatId, request.ChannelId.Value);
                }
                await botClient.SendMessage(chatId,
                    $"✅ You started chat with channel {channel.Name} (ID: {channel.Id}). Chat is now active.");
                meshtasticService.SendPrivateChannelTextMessage(
                    MeshtasticService.GetNextMeshtasticMessageId(),
                      "✅ Chat request approved. You can send messages.",
                      replyToMessageId: null,
                      relayGatewayId: null,
                      hopLimit: int.MaxValue,
                      channel);
            }
        }

        private async Task HandleNestedResult(TgResult res)
        {
            if (res.MeshMessage != null)
            {
                await EnsureMeshSenderCreated().SendAndTrackMeshtasticMessage(
                    res.MeshMessage.Recipient,
                    res.MeshMessage.TelegramChatId,
                    res.MeshMessage.TelegramMessageId,
                    res.MeshMessage.Text);
            }
            if (res.NetworkWithUpdatedGateways != null)
            {
                NetworkGatewayListChanged ??= [];
                foreach (var networkId in res.NetworkWithUpdatedGateways)
                {
                    NetworkGatewayListChanged.Add(networkId);
                }
            }
            if (res.NetworkWithUpdatedPublicChannels != null)
            {
                NetworkPublicChannelsChanged ??= [];
                foreach (var networkId in res.NetworkWithUpdatedPublicChannels)
                {
                    NetworkPublicChannelsChanged.Add(networkId);
                }
            }
            if (res.NetworksUpdated)
            {
                NetworksUpdated = true;
            }
        }

        private static string GetTelegramUserName(User usr)
        {
            return (usr.Username ?? $"{usr.FirstName} {usr.LastName}").Trim();
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

            var textToMesh = $"{TrimTelegramUserName(userName)}: {text}";

            if (!MeshtasticService.CanSendMessage(text))
            {
                await botClient.SendMessage(
                    chatId,
                    $"Message is too long to send to a Meshtastic device. Please keep it under {MeshtasticService.MaxTextMessageBytes} bytes (English letters: 1 byte, Cyrillic: 2 bytes, emoji: 4 bytes).",
                    replyParameters: new ReplyParameters
                    {
                        AllowSendingWithoutReply = false,
                        ChatId = chatId,
                        MessageId = msgId
                    });
                return;
            }

            var activeSession = botCache.GetActiveChatSession(chatId);
            if (activeSession != null)
            {
                var recipient = await GetChatSessionRecipient(activeSession);

                await EnsureMeshSenderCreated().SendAndTrackMeshtasticMessages(
                    [recipient],
                    chatId,
                    msgId,
                    replyToTelegramMessageId,
                    textToMesh);

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
            await EnsureMeshSenderCreated().SendAndTrackMeshtasticMessages(
                channelRegs.AsEnumerable<IRecipient>().Concat(devRegs),
                chatId,
                msgId,
                replyToTelegramMessageId,
                textToMesh);
        }

        private async Task<IRecipient> GetChatSessionRecipient(DeviceOrChannelId activeSession)
        {
            return activeSession.DeviceId != null
                ? await registrationService.GetDeviceAsync(activeSession.DeviceId.Value)
                : await registrationService.GetChannelAsync(activeSession.ChannelId.Value);
        }

        private static string TrimTelegramUserName(string userName)
        {
            return StringHelper.Truncate(userName, TrimUserNamesToLength);
        }

        public static void Register(IServiceCollection services)
        {
            services.AddScoped(s =>
            {
                var options = s.GetRequiredService<IOptions<TBotOptions>>();
                return new TelegramBotClient(options.Value.TelegramApiToken);
            });
            services.AddSingleton<MeshtasticService>();
            services.AddSingleton<BotCache>();
            services.AddScoped<RegistrationService>();
            services.AddScoped<TgBotService>();
            services.AddScoped<AdminBotService>();
            services.AddScoped<TgCommandBotService>();
            services.AddScoped<MeshtasticBotService>();
            services.AddScoped<MeshtasticBotMsgStatusTracker>();
        }

        public Task ProcessInboundTelegramUpdate(string payload)
        {
            var update = JsonSerializer.Deserialize<Update>(payload, JsonBotAPI.Options);
            logger.LogDebug("Processing inbound Telegram message: {Payload}", payload);
            return HandleTgUpdate(update);
        }

        public async Task NotifyGatewayDemotedDueToInactivity(long deviceId, string deviceName)
        {
            var chatIds = await registrationService.GetChatsByDeviceIdCached(deviceId);
            if (chatIds.Count == 0)
            {
                return;
            }

            var hexId = MeshtasticService.GetMeshtasticNodeHexId(deviceId);
            var text = $"\u26a0\ufe0f Gateway *{StringHelper.EscapeMd(deviceName)}* ({hexId}) has been automatically demoted due to inactivity. " +
                       "It has not been seen on the network for an extended period. " +
                       "Use /promote_to_gateway to restore gateway status once the device is back online.";

            foreach (var chatId in chatIds)
            {
                await botClient.TrySendMessage(
                    registrationService,
                    logger,
                    chatId,
                    text,
                    parseMode: ParseMode.Markdown);
            }
        }
        internal async Task NotifyNewGatewaySeen(long gatewayId)
        {
            var chatIds = new List<long>();
            var cachedChatId = botCache.GetGatewayRegistraionChat(gatewayId);
            if (cachedChatId.HasValue)
            {
                chatIds.Add(cachedChatId.Value);
            }
            else
            {
                chatIds = await registrationService.GetChatsByDeviceIdCached(gatewayId);
            }

            if (chatIds.Count == 0)
            {
                return;
            }
            foreach (var id in chatIds)
            {
                await botClient.TrySendMessage(
                    registrationService,
                    logger,
                    id,
                    $"First packet received from gateway {MeshtasticService.GetMeshtasticNodeHexId(gatewayId)}. Gateway is now online.");
            }
        }
    }
}
