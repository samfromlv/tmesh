using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TBot.Bot
{
    public class TgBotService(
        TelegramBotClient botClient,
        IOptions<TBotOptions> options,
        RegistrationService registrationService,
        BotCache botCache,
        ILogger<TgBotService> logger,
        IServiceProvider services)
    {

        const int TrimUserNamesToLength = 8;
        public const string NoDeviceOrChannelMessage = "No registered devices or channels. You can register a new device with the /add_device command or channel with /add_channel command. Please remove the bot from the group if you don't need it.";
        public const string NoDeviceMessage = "No registered devices. You can register a new device with the /add_device command.";
        private readonly TBotOptions _options = options.Value;
        public static readonly JsonSerializerOptions IdentedOptions = new()
        {
            WriteIndented = true,
        };

        public HashSet<int> NetworkGatewayListChanged { get; private set; }

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

            var channelRegs = await registrationService.GetChannelKeysByChatIdCached(chatId);
            var devRegs = await registrationService.GetDeviceKeysByChatIdCached(chatId);
            if (devRegs.Count == 0
                && channelRegs.Count == 0)
            {
                return false;
            }

            var emojis = string.Join(null,
                    msgReaction.NewReaction.Select(ConvertReactionType)
                );

            var userName = GetTelegramUserName(msgReaction.User);
            var trimmedUserName = TrimTelegramUserName(userName);
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

            if (chatStateWithData?.State != ChatState.Default)
            {
                var res = await services.GetRequiredService<TgCommandBotService>().ProcessCommandChat(msg, chatStateWithData);
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
            await EnsureMeshSenderCreated().SendAndTrackMeshtasticMessages(
                channelRegs.AsEnumerable<IRecipient>().Concat(devRegs),
                chatId,
                msgId,
                replyToTelegramMessageId,
                textToMesh);
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
            var text = $"\u26a0\ufe0f Gateway *{deviceName}* ({hexId}) has been automatically demoted due to inactivity. " +
                       "It has not been seen on the network for an extended period. " +
                       "Use /promote_to_gateway to restore gateway status once the device is back online.";

            foreach (var chatId in chatIds)
            {
                await botClient.SendMessage(chatId, text, parseMode: ParseMode.Markdown);
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
                await botClient.SendMessage(id, $"First packet received from gateway {MeshtasticService.GetMeshtasticNodeHexId(gatewayId)}. Gateway is now online.");
            }
        }
    }
}
