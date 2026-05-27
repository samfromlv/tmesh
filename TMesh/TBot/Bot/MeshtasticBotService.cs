using Google.Protobuf.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using System.Text;
using TBot.Analytics;
using TBot.Analytics.Models;
using TBot.Database;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
using TBot.Models.ChatSession;
using TBot.Models.MeshMessages;
using TBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TBot.Bot
{
    public class MeshtasticBotService(
        TelegramBotClient botClient,
        IOptions<TBotOptions> options,
        RegistrationService registrationService,
        MeshtasticBotMsgStatusTracker meshSender,
        MeshtasticService meshtasticService,
        BotCache botCache,
        ILogger<MeshtasticBotService> logger,
        IServiceProvider services,
        TBotDbContext db,
        UptimeService uptimeService)
    {
        private const int MaxFakeMsgReplyPer5Min = 30;
        private readonly TBotOptions _options = options.Value;

        public List<MeshtasticMessageStatus> TrackedMessages => meshSender.TrackedMessages;


        public async Task ProcessInboundMeshtasticMessage(MeshMessage message)
        {
            botCache.StoreDeviceGateway(message);
            if (message.ChannelId.HasValue && message.IsSingleDeviceChannel && message.TMeshGatewayId.HasValue)
            {
                botCache.StoreChannelGateway((int)message.ChannelId.Value, message.TMeshGatewayId.Value, message.DeviceId, message.GetSuggestedReplyHopLimit());
            }
            switch (message.MessageType)
            {
                case MeshMessageType.NodeInfo:
                    await ProcessInboundNodeInfo((NodeInfoMessage)message);
                    break;
                case MeshMessageType.Text:
                    await ProcessInboundMeshTextMessage((TextMessage)message);
                    break;
                case MeshMessageType.EncryptedDirectMessage:
                    await SendNoPublicKeyNak(message);
                    break;
                case MeshMessageType.TraceRoute:
                    await ProcessInboundTraceRoute((TraceRouteMessage)message);
                    break;
                case MeshMessageType.Position:
                    await ProcessInboundPositionMessage((PositionMessage)message);
                    break;
                case MeshMessageType.DeviceMetrics:
                    await ProcessInboundDeviceMetricsMessage((DeviceMetricsMessage)message);
                    break;
                case MeshMessageType.AckMessage:
                default:
                    logger.LogWarning("Received unsupported Meshtastic message type: {MessageType}", message.MessageType);
                    break;
            }
        }

        private async Task SendNoPublicKeyNak(MeshMessage message)
        {
            var primaryChannel = message.DecodedBy.IsPublicChannel ? message.DecodedBy : null;

            if (primaryChannel == null)
            {
                primaryChannel = await registrationService.GetNetworkPrimaryChannelCached(message.NetworkId);
                if (primaryChannel == null)
                {
                    logger.LogWarning("Received encrypted direct message for network {NetworkId} without primary channel, cannot send ack", message.NetworkId);
                    return;
                }
            }

            meshtasticService.NakNoPubKeyMeshtasticMessage(message, meshSender.GetReplyGatewayId(message), primaryChannel);
        }

        private async Task ProcessInboundDeviceMetricsMessage(DeviceMetricsMessage message)
        {
            Device device = null;
            if (message.NeedAck)
            {
                device = (message.DecodedBy as Device) ?? await registrationService.GetDeviceAsync(message.DeviceId);
                if (device != null)
                {
                    meshtasticService.AckMeshtasticMessage(message, device, meshSender.GetReplyGatewayId(message));
                }
            }
            var analyticsService = services.GetService<AnalyticsService>();
            if (analyticsService == null)
            {
                return;
            }

            var network = await registrationService.GetNetwork(message.NetworkId);
            if (network == null
                || !network.SaveAnalytics)
            {
                return;
            }

            device ??= await registrationService.GetDeviceAsync(message.DeviceId);
            if (device?.LocationUpdatedUtc == null
                || !device.IsLocationPublic)
            {
                return;
            }
            logger.LogDebug("Processing inbound Meshtastic message: {Message}", message);

            var metrics = new DeviceMetric
            {
                NetworkId = device.NetworkId,
                DeviceId = (uint)message.DeviceId,
                Timestamp = Instant.FromDateTimeUtc(DateTime.UtcNow),
                Latitude = device.Latitude ?? 0,
                Longitude = device.Longitude ?? 0,
                LocationUpdatedUtc = Instant.FromDateTimeUtc(DateTime.SpecifyKind(device.LocationUpdatedUtc.Value, DateTimeKind.Utc)),
                AccuracyMeters = device.AccuracyMeters,
                ChannelUtil = message.ChannelUtilization,
                AirUtil = message.AirUtilization,
            };

            await analyticsService.RecordEventAsync(metrics);
        }

        private async Task ProcessInboundPositionMessage(PositionMessage message)
        {
            var device = (message.DecodedBy as Device) ?? await registrationService.GetDeviceAsync(message.DeviceId);
            if (device == null)
            {
                return;
            }
            if (message.NeedAck)
            {
                meshtasticService.AckMeshtasticMessage(message, device, meshSender.GetReplyGatewayId(message));
            }
            logger.LogDebug("Processing inbound Meshtastic message: {Message}", message);
            device.LocationUpdatedUtc = DateTime.UtcNow;
            device.Longitude = message.Longitude;
            device.Latitude = message.Latitude;
            device.IsLocationPublic = message.DecodedBy.IsPublicChannel;
            device.AccuracyMeters = (int)Math.Round(message.AccuracyMeters);
            await registrationService.SaveAssumeChanged(device);
            if (!message.SentToOurNodeId)
            {
                return;
            }

            var chatIds = await GetChatsForDevice(message.DeviceId);

            if (chatIds.Count == 0)
            {
                MaybeSendNotRegisteredResponse(message, device);
                return;
            }

            foreach (var chatId in chatIds)
            {
                var msg = await TrySendMessage(
                    chatId,
                    $"{device.NodeName} sent a location:");
                if (msg == null) continue;

                await botClient.SendLocation(
                        chatId,
                        message.Latitude,
                        message.Longitude,
                        heading: message.HeadingDegrees,
                        horizontalAccuracy: Math.Min(message.AccuracyMeters, 1500));
            }

        }

        private async Task<List<long>> GetChatsForDevice(long deviceId)
        {
            List<long> chatIds;

            var activeSessionChatId = await botCache.GetActiveChatSessionForDevice(deviceId, db);
            if (activeSessionChatId != null)
            {
                chatIds = [activeSessionChatId.Value];
            }
            else
            {
                chatIds = await registrationService.GetChatsByDeviceIdCached(deviceId);
            }

            return chatIds;
        }

        private async Task ProcessInboundMeshTextMessage(TextMessage message)
        {
            logger.LogDebug("Processing inbound Meshtastic message: {Message}", message);
            if (message.IsDirectMessage)
            {
                await ProcessInboundDirectMeshTextMessage(message);
            }
            else if (message.ChannelId != null)
            {
                await ProcessInboundPrivateChannelMeshTextMessage(message);
            }
            else
            {
                await ProcessInboundPublicMeshTextMessage(message);
            }

        }



        private async ValueTask ProcessPublicTextForChatSession(TextMessage meshMsg)
        {
            if (!meshMsg.DecodedBy.IsPublicChannel)
                return;

            var activeChatId = await botCache.GetActiveChatSessionForPublicChannel(meshMsg.DecodedBy.RecipientPublicChannelId.Value, db);
            if (activeChatId == null)
            {
                return;
            }
            var text = meshMsg.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            var channel = (PublicChannel)meshMsg.DecodedBy;
            var channelName = channel.Name;

            bool sentReply = false;

            var device = await registrationService.GetDeviceAsync(meshMsg.DeviceId);
            var deviceName = device != null ? device.NodeName : MeshtasticService.GetMeshtasticNodeHexId(meshMsg.DeviceId);

            var colorSymbols = "⚪⚫🔴🟠🟡🟢🔵🟣";
            var colorSymbol = colorSymbols[(int)(meshMsg.DeviceId % colorSymbols.Length)];

            if (meshMsg.ReplyTo != 0)
            {
                var replyToStatus = botCache.GetMeshMessageStatus(meshMsg.ReplyTo);
                if (replyToStatus?.TelegramChatId == activeChatId.Value)
                {
                    var msg = await TrySendMessage(
                        chatId: replyToStatus.TelegramChatId,
                        text: $"{colorSymbol}{deviceName} [#{channel.Name}]: {text}",
                        replyParameters: new ReplyParameters
                        {
                            AllowSendingWithoutReply = true,
                            ChatId = replyToStatus.TelegramChatId,
                            MessageId = replyToStatus.TelegramMessageId,
                        });

                    if (msg == null)
                        return;

                    var status = new MeshtasticMessageStatus
                    {
                        TelegramChatId = replyToStatus.TelegramChatId,
                        TelegramMessageId = msg.Id,
                        MeshMessages = new Dictionary<long, DeliveryStatusWithRecipientId>
                            {
                                { meshMsg.Id, new DeliveryStatusWithRecipientId
                                    {
                                        RecipientId = channel.Id,
                                        Type = RecipientType.PublicChannel,
                                        Status = DeliveryStatus.Delivered,
                                    }
                                }
                            },
                        BotReplyId = null,
                    };

                    meshSender.StoreTelegramMessageStatus(channel.NetworkId, replyToStatus.TelegramChatId,
                        msg.Id,
                        status);

                    botCache.StoreMeshMessageStatus(channel.NetworkId, meshMsg.Id, status);
                    sentReply = true;
                }
            }

            if (!sentReply)
            {
                var tgMsg = await TrySendMessage(
                    chatId: activeChatId.Value,
                    text: $"{colorSymbol}{deviceName} [#{channel.Name}]: {text}");

                if (tgMsg == null) return;

                var status = new MeshtasticMessageStatus
                {
                    TelegramChatId = activeChatId.Value,
                    TelegramMessageId = tgMsg.Id,
                    MeshMessages = new Dictionary<long, DeliveryStatusWithRecipientId>
                            {
                                { meshMsg.Id, new DeliveryStatusWithRecipientId
                                    {
                                        RecipientId = channel.Id,
                                        Type = RecipientType.PublicChannel,
                                        Status = DeliveryStatus.Delivered,
                                    }
                                }
                            },
                    BotReplyId = null
                };

                meshSender.StoreTelegramMessageStatus(channel.NetworkId, activeChatId.Value, tgMsg.Id, status);
                botCache.StoreMeshMessageStatus(channel.NetworkId, meshMsg.Id, status);
            }
        }


        private async Task ProcessInboundPublicMeshTextMessage(TextMessage message)
        {
            await ProcessPublicTextForChatSession(message);

            if (message.DeviceId == _options.MeshtasticNodeId)
            {
                CheckPublicTextForFakeMessage(message);
                return;
            }

            if (_options.ReplyToPublicPingsViaDirectMessage
                && _options.PingWords.Length > 0)
            {
                var text = message.Text.Trim();
                bool isPing = _options.PingWords.Any(pingWord => string.Equals(text, pingWord, StringComparison.OrdinalIgnoreCase));
                if (!isPing || message.ReplyTo != 0)
                {
                    return;
                }

                var device = await registrationService.GetDeviceAsync(message.DeviceId);

                if (device == null)
                {
                    await SendNoPublicKeyNak(message);
                    return;
                }

                var network = await registrationService.GetNetwork(device.NetworkId);

                if (!network.DisablePongs)
                {
                    meshtasticService.SendDirectTextMessage(
                        message.DeviceId,
                        device.NetworkId,
                        device.PublicKey,
                        GetPingReplyText(network),
                        replyToMessageId: null,//Message is from public channel and we are sending direct reply, so no replyToMessageId
                        relayGatewayId: meshSender.GetReplyGatewayId(message),
                        hopLimit: message.GetSuggestedReplyHopLimit());

                    meshtasticService.AddStat(new Shared.Models.MeshStat
                    {
                        NetworkId = device.NetworkId,
                        PongSent = 1,
                    });
                }
            }
        }

        private void CheckPublicTextForFakeMessage(TextMessage message)
        {
            var isValidMessage = botCache.IsMessageSentByOurNode(message.Id);
            if (!isValidMessage
                && uptimeService.Uptime.TotalMinutes > 10 /*If restarted can hear own message from map mqtt downlink*/)
            {
                var fakeMsgReplyLast5Min = meshtasticService.AggregateStartFrom<int>(
                    message.NetworkId,
                    DateTime.UtcNow.AddMinutes(-5),
                    (stat, sum) =>
                        {
                            sum += stat.FakeMsgReply;
                            return sum;
                        });


                if (!string.IsNullOrEmpty(_options.Texts.FakeMessageWarningReply)
                    && fakeMsgReplyLast5Min < MaxFakeMsgReplyPer5Min)
                {
                    var newMsgId = MeshtasticService.GetNextMeshtasticMessageId();
                    botCache.StoreMessageSentByOurNode(newMsgId);
                    meshtasticService.SendPublicTextMessage(
                        newMessageId: newMsgId,
                        text: _options.Texts.FakeMessageWarningReply,
                        relayGatewayId: null,
                        hopLimit: int.MaxValue,
                        publicChannelName: message.DecodedBy is PublicChannel pc
                            ? pc.Name
                            : MeshtasticService.UnknownChannelName,
                        message.DecodedBy,
                        replyToMessageId: message.Id);

                    meshtasticService.AddStat(new Shared.Models.MeshStat
                    {
                        FakeMsgReply = 1,
                    });
                }
            }
        }

        private string GetPingReplyText(Network network)
        {
            var reply = String.IsNullOrEmpty(network?.Url) ?
                _options.Texts.PingReply ?? "pong"
                : (_options.Texts.PingReplyWithNetworkUrl ?? "pong") + $" {network.Url}";
            return reply ?? "pong";
        }


        void HandleHelpCommand(MeshMessage msg, IRecipient recipient)
        {
            var helpText =
                "/ping - bot will respond with pong\n" +
                "/chat @tg_user - starts chat with Telegram user\n" +
                "/end_chat - ends active chat \n" +
                $"@{_options.TelegramBotUserName} - Telegram bot\n" +
                "tmesh.ru - more help";

            meshtasticService.SendTextMessageToDeviceOrPrivateChannel(
                recipient,
                helpText,
                replyToMessageId: msg.Id,
                relayGatewayId: meshSender.GetReplyGatewayId(msg),
                hopLimit: msg.GetSuggestedReplyHopLimit());
        }


        private async Task ProcessInboundPrivateChannelMeshTextMessage(TextMessage message)
        {
            var channel = await registrationService.GetChannelAsync((int)message.ChannelId.Value);
            if (channel == null)
            {
                //how we have decrypted channel ID but can't find it in the database
                return;
            }

            var device = await registrationService.GetDeviceAsync(message.DeviceId);

            var deviceName = device != null ? device.NodeName : MeshtasticService.GetMeshtasticNodeHexId(message.DeviceId);

            string cmdText = null;
            if (message.Text != null
                && message.Text.Trim().Length > 1
                && message.Text.StartsWith('/'))
            {
                cmdText = message.Text[1..].Trim();
            }

            // Handle /chat @username command from Mesh device
            if (cmdText != null && cmdText.StartsWith("chat ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = cmdText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var targetHandle = parts[1];
                    await HandleChatRequestFromMesh(message, device, channel, targetHandle);
                    return;
                }
            }
            else if (cmdText != null && cmdText.StartsWith("end_chat", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEndChatRequstFromMesh(message, device, channel);
                return;
            }

            var trimmedText = message.Text?.Trim();
            // Handle "yes" approval from Mesh device
            if (trimmedText != null
                && trimmedText.Length == RegistrationService.CodeLength
                && trimmedText.All(Char.IsDigit))
            {
                var pendingRequest = botCache.GetPendingChannelChatRequest_TgToMesh((int)message.ChannelId.Value);
                if (pendingRequest != null)
                {
                    if (trimmedText.Equals(pendingRequest.Code, StringComparison.InvariantCultureIgnoreCase))
                    {
                        await HandleChatApprovalFromMesh(message, device, channel, pendingRequest.ChatId);
                        return;
                    }
                    else
                    {
                        meshtasticService.SendTextMessageToDeviceOrPrivateChannel(
                            channel,
                            $"Invalid code. To approve reply with code {pendingRequest.Code}.",
                            replyToMessageId: message.Id,
                            relayGatewayId: meshSender.GetReplyGatewayId(message),
                            hopLimit: message.GetSuggestedReplyHopLimit());

                        return;
                    }
                }
            }



            if (cmdText != null &&
                _options.PingWords.Any(pingWord => string.Equals(cmdText, pingWord, StringComparison.OrdinalIgnoreCase)))
            {
                var network = await registrationService.GetNetwork(channel.NetworkId);

                meshtasticService.SendPrivateChannelTextMessage(
                    MeshtasticService.GetNextMeshtasticMessageId(),
                    GetPingReplyText(network),
                    replyToMessageId: message.Id,
                    relayGatewayId: meshSender.GetReplyGatewayId(message),
                    hopLimit: message.GetSuggestedReplyHopLimit(),
                    channel);

                meshtasticService.AddStat(new Shared.Models.MeshStat
                {
                    NetworkId = channel.NetworkId,
                    PongSent = 1,
                });
                return;
            }
            else if (cmdText != null && cmdText.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                HandleHelpCommand(message, channel);
                return;
            }

            List<long> chatIds;

            var activeChatId = await botCache.GetActiveChatSessionForChannel((int)message.ChannelId.Value, db);
            if (activeChatId != null)
            {
                chatIds = [activeChatId.Value];
            }
            else
            {
                chatIds = await registrationService.GetChatsByChannelIdCached(channel.Id);
            }

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
                var replyToStatus = botCache.GetMeshMessageStatus(message.ReplyTo);
                if (replyToStatus != null
                        && chatIds.Any(x => x == replyToStatus.TelegramChatId))
                {
                    var msg = await TrySendMessage(
                        chatId: replyToStatus.TelegramChatId,
                        text: $"{deviceName} [#{channel.Name}]: {text}",
                        replyParameters: new ReplyParameters
                        {
                            AllowSendingWithoutReply = true,
                            ChatId = replyToStatus.TelegramChatId,
                            MessageId = replyToStatus.TelegramMessageId,
                        });

                    if (msg == null)
                        return;

                    var status = new MeshtasticMessageStatus
                    {
                        TelegramChatId = replyToStatus.TelegramChatId,
                        TelegramMessageId = msg.Id,
                        MeshMessages = new Dictionary<long, DeliveryStatusWithRecipientId>
                            {
                                { message.Id, new DeliveryStatusWithRecipientId
                                    {
                                        RecipientId = channel.Id,
                                        Type = RecipientType.PrivateChannel,
                                        Status = DeliveryStatus.Delivered,
                                    }
                                }
                            },
                        BotReplyId = null,
                    };

                    meshSender.StoreTelegramMessageStatus(channel.NetworkId, replyToStatus.TelegramChatId,
                        msg.Id,
                        status);

                    botCache.StoreMeshMessageStatus(channel.NetworkId, message.Id, status);
                    sentReply = true;
                }
            }

            if (!sentReply)
            {
                foreach (var chatId in chatIds)
                {
                    var msg = await TrySendMessage(
                        chatId: chatId,
                        text: $"{deviceName} [#{channel.Name}]: {text}");

                    if (msg == null) continue;

                    var status = new MeshtasticMessageStatus
                    {
                        TelegramChatId = chatId,
                        TelegramMessageId = msg.Id,
                        MeshMessages = new Dictionary<long, DeliveryStatusWithRecipientId>
                            {
                                { message.Id, new DeliveryStatusWithRecipientId
                                    {
                                        RecipientId = channel.Id,
                                        Type = RecipientType.PrivateChannel,
                                        Status = DeliveryStatus.Delivered,
                                    }
                                }
                            },
                        BotReplyId = null
                    };

                    meshSender.StoreTelegramMessageStatus(channel.NetworkId, chatId, msg.Id, status);
                    botCache.StoreMeshMessageStatus(channel.NetworkId, message.Id, status);
                }
            }

            if (!message.IsEmoji && meshtasticService.GetTotalQueueLength(channel.NetworkId) < _options.MaxQueueLengthForChannelAckEmojis)
            {
                meshtasticService.SendPrivateChannelTextMessage(
                    MeshtasticService.GetNextMeshtasticMessageId(),
                    "✓",
                    message.Id,
                    relayGatewayId: meshSender.GetReplyGatewayId(message),
                    hopLimit: message.GetSuggestedReplyHopLimit(),
                    channel,
                    isEmoji: true);
            }
        }



        private Task<Message> TrySendMessage(
          long chatId,
          string text,
          ReplyParameters replyParameters = null,
          ParseMode parseMode = ParseMode.None)
        {
            return botClient.TrySendMessage(
                 registrationService,
                 logger,
                 chatId,
                 text,
                 replyParameters,
                 parseMode);
        }

        private async Task ProcessInboundDirectMeshTextMessage(TextMessage message)
        {
            var device = message.DecodedBy as Device;

            if (device == null)
            {
                throw new ArgumentNullException(nameof(device), "Device cannot be null for Text messages");
            }
            if (message.NeedAck)
            {
                meshtasticService.AckMeshtasticMessage(
                    message,
                    device,
                    meshSender.GetReplyGatewayId(message));
            }

            string cmdText = null;
            if (message.Text != null
                && message.Text.Trim().Length > 1
                && message.Text.StartsWith('/'))
            {
                cmdText = message.Text[1..].Trim();
            }

            // Handle /chat @username command from Mesh device
            if (cmdText != null && cmdText.StartsWith("chat", StringComparison.OrdinalIgnoreCase))
            {
                var firstSpaceIndex = cmdText.IndexOf(' ');
                if (firstSpaceIndex > 0)
                {
                    var targetHandle = cmdText[(firstSpaceIndex + 1)..].Trim();
                    await HandleChatRequestFromMesh(message, device, device, targetHandle);
                    return;
                }
                else
                {
                    meshtasticService.SendDirectTextMessage(
                        message.DeviceId,
                        device.NetworkId,
                        device.PublicKey,
                        $"Use /chat @<tg_username> or /chat <tg_group_name>",
                        replyToMessageId: message.Id,
                        relayGatewayId: meshSender.GetReplyGatewayId(message),
                        hopLimit: message.GetSuggestedReplyHopLimit());
                }
            }
            else if (cmdText != null && cmdText.StartsWith("end_chat", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEndChatRequstFromMesh(message, device, device);
                return;
            }
            else if (cmdText != null && cmdText.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                HandleHelpCommand(message, device);
                return;
            }

            var trimmedText = message.Text?.Trim();
            if (trimmedText != null
                && trimmedText.Length == RegistrationService.CodeLength
                && trimmedText.All(char.IsDigit))
            {
                var pendingRequest = botCache.GetPendingDeviceChatRequest_TgToMesh(message.DeviceId);
                if (pendingRequest != null)
                {
                    if (trimmedText.Equals(pendingRequest.Code, StringComparison.InvariantCultureIgnoreCase))
                    {
                        await HandleChatApprovalFromMesh(message, device, device, pendingRequest.ChatId);
                        return;
                    }
                    else
                    {
                        meshtasticService.SendDirectTextMessage(
                            message.DeviceId,
                            device.NetworkId,
                            device.PublicKey,
                            $"Invalid code. To approve reply with code {pendingRequest.Code}, 'no' to reject.",
                            replyToMessageId: message.Id,
                            relayGatewayId: meshSender.GetReplyGatewayId(message),
                            hopLimit: message.GetSuggestedReplyHopLimit());
                        return;
                    }
                }
            }

            if (cmdText != null &&
                _options.PingWords.Any(pingWord => string.Equals(cmdText, pingWord, StringComparison.OrdinalIgnoreCase)))
            {
                var network = await registrationService.GetNetwork(device.NetworkId);

                meshtasticService.SendDirectTextMessage(
                    message.DeviceId,
                    device.NetworkId,
                    device.PublicKey,
                    GetPingReplyText(network),
                    replyToMessageId: message.Id,
                    relayGatewayId: meshSender.GetReplyGatewayId(message),
                    hopLimit: message.GetSuggestedReplyHopLimit());

                meshtasticService.AddStat(new Shared.Models.MeshStat
                {
                    NetworkId = device.NetworkId,
                    PongSent = 1,
                });
                return;
            }

            var chatIds = await GetChatsForDevice(message.DeviceId);
            if (chatIds.Count == 0)
            {
                if (message.ReplyTo == 0)
                {
                    MaybeSendNotRegisteredResponse(message, device);
                }
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
                var replyToStatus = botCache.GetMeshMessageStatus(message.ReplyTo);
                if (replyToStatus != null
                        && chatIds.Any(x => x == replyToStatus.TelegramChatId))
                {
                    var msg = await TrySendMessage(
                        chatId: replyToStatus.TelegramChatId,
                        text: $"{device.NodeName}: {text}",
                        replyParameters: new ReplyParameters
                        {
                            AllowSendingWithoutReply = true,
                            ChatId = replyToStatus.TelegramChatId,
                            MessageId = replyToStatus.TelegramMessageId,
                        });

                    if (msg == null)
                        return;


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

                    meshSender.StoreTelegramMessageStatus(device.NetworkId, replyToStatus.TelegramChatId,
                        msg.Id,
                        status);

                    botCache.StoreMeshMessageStatus(device.NetworkId, message.Id, status);

                    sentReply = true;
                }
            }

            if (!sentReply)
            {
                if (chatIds.Count == 0)
                {
                    MaybeSendNotRegisteredResponse(message, device);
                    return;
                }

                foreach (var chatId in chatIds)
                {
                    var msg = await TrySendMessage(
                        chatId: chatId,
                        text: $"{device.NodeName}: {text}");

                    if (msg == null) continue;

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

                    meshSender.StoreTelegramMessageStatus(device.NetworkId, chatId, msg.Id, status);
                    botCache.StoreMeshMessageStatus(device.NetworkId, message.Id, status);
                }
            }
        }


        private async Task ProcessInboundTraceRoute(TraceRouteMessage message)
        {
            var primaryChannel = await registrationService.GetNetworkPrimaryChannelCached(message.NetworkId);
            if (primaryChannel == null)
            {
                logger.LogWarning("Received trace route message for network {NetworkId} without primary channel, ignoring", message.NetworkId);
                return;
            }

            if (message.IsTowards)
            {
                await ProcessTowardsTrace(message, primaryChannel);
            }
            else
            {
                await ProcessTraceResponse(message);
            }
        }

        private async Task ProcessTraceResponse(TraceRouteMessage message)
        {
            var chatId = botCache.GetTraceRouteChat(message.RequestId);
            if (chatId != null)
            {
                var text = await FormatTraceRouteMessage(message);
                await TrySendMessage(
                    chatId: chatId.Value,
                    text: $"Trace response:\n" + text);
            }
        }

        private async Task ProcessTowardsTrace(TraceRouteMessage message, PublicChannel primaryChannel)
        {
            if (message.WantsResponse)
            {
                meshtasticService.SendTraceRouteToUsResponse(message, meshSender.GetReplyGatewayId(message), primaryChannel, primaryChannel.Name);
            }
            var device = await registrationService.GetDeviceAsync(message.DeviceId);
            if (device == null)
            {
                return;
            }

            var text = await FormatTraceRouteMessage(message);

            var chatIds = await GetChatsForDevice(message.DeviceId);
            foreach (var chatId in chatIds)
            {
                await TrySendMessage(
                    chatId: chatId,
                    text: $"Trace:\n" + text);
            }
        }

        private async Task ProcessInboundNodeInfo(NodeInfoMessage message)
        {
            var res = await registrationService.SaveDeviceAsync(
                message.DeviceId,
                message.NetworkId,
                message.NodeName,
                message.NodeInfo.HardwareModel,
                message.NodeInfo.MacAddr,
                message.PublicKey,
                message.Id,
                message.DecodedBy.RecipientPublicChannelId,
                MeshtasticService.ConvertDeviceRole(message.NodeInfo.Role));

            if (message.NeedAck && res.device != null && res.device.PublicKey != null)
            {
                meshtasticService.AckMeshtasticMessage(
                  message,
                  res.device,
                  meshSender.GetReplyGatewayId(message));
            }

            if (res.res == SaveResult.Inserted)
            {
                var network = await registrationService.GetNetwork(message.NetworkId);
                if (network != null
                      && !network.DisableWelcomeMessage
                      && (!string.IsNullOrEmpty(network.Url)
                            || !string.IsNullOrEmpty(network.CommunityUrl)
                            || !string.IsNullOrEmpty(network.WelcomeUrl)))
                {
                    string messageText;
                    if (string.IsNullOrEmpty(network.WelcomeUrl))
                    {
                        var template = _options.Texts.NewDeviceWelcomeMessage_Template ?? "Welcome!{settings}{community}";
                        var settingsPart = _options.Texts.NewDeviceWelcomeMessage_Settings ?? " Settings: {url}.";
                        var communityPart = _options.Texts.NewDeviceWelcomeMessage_Community ?? " Community: {url}";

                        var msgText = new StringBuilder(template);
                        msgText = msgText
                          .Replace("{settings}", string.IsNullOrEmpty(network.Url) ? "" : settingsPart.Replace("{url}", network.Url))
                          .Replace("{community}", string.IsNullOrEmpty(network.CommunityUrl) ? "" : communityPart.Replace("{url}", network.CommunityUrl));
                        messageText = msgText.ToString();
                    }
                    else
                    {
                        var welcomePart = _options.Texts.NewDeviceWelcomeMessage_WelcomeUrl ?? "Welcome! Setting and community: {url}";
                        messageText = welcomePart.Replace("{url}", network.WelcomeUrl);
                    }

                    var primaryChannel = message.DecodedBy.IsPublicChannel ? message.DecodedBy : null;

                    if (primaryChannel == null)
                    {
                        primaryChannel = await registrationService.GetNetworkPrimaryChannelCached(message.NetworkId);
                    }
                    if (primaryChannel == null)
                    {
                        return;
                    }

                    meshtasticService.SendVirtualNodeInfo(
                         (primaryChannel as PublicChannel)?.Name ?? MeshtasticService.UnknownChannelName,
                         primaryChannel,
                         message.GetSuggestedReplyHopLimit(),
                         destinationDeviceId: message.DeviceId,
                         meshSender.GetReplyGatewayId(message));

                    meshtasticService.SendDirectTextMessage(
                        message.DeviceId,
                        message.NetworkId,
                        message.PublicKey,
                        messageText,
                        replyToMessageId: null,
                        relayGatewayId: meshSender.GetReplyGatewayId(message),
                        hopLimit: message.GetSuggestedReplyHopLimit(),
                        sendDelay: TimeSpan.FromSeconds(10));

                    meshtasticService.AddStat(new Shared.Models.MeshStat
                    {
                        NetworkId = message.NetworkId,
                        WelcomeMessagesSent = 1,
                    });
                }
            }
            else if (res.res == SaveResult.SecurityErrorKeyPinned
                || res.res == SaveResult.SecurityErrorHardwareNotMatching)
            {
                var device = res.device;
                if (device == null)
                {
                    return;
                }

                //Node public key do not match our records, delivery of messages to this node will not work
                //we need to warn users that his public key was changed and he need to remove device and readded it
                var chatIds = await registrationService.GetChatsByDeviceIdCached(message.DeviceId);

                if (chatIds.Count != 0)
                {
                    var msgTxt = res.res switch
                    {
                        SaveResult.SecurityErrorKeyPinned => $"Warning: The new public key was detected for device {device.NodeName}. If you have recently reset your device or changed encryption keys, please remove the device (using /remove_device command) and add it back for messaging to work. Public keys are not updated automatically after device first registration due security reasons. If you haven't changed the keys or reset your device please take it as a warning, some node in the network is using your device id.",
                        SaveResult.SecurityErrorHardwareNotMatching => $"Warning: The hardware model reported by device {device.NodeName} do not match the one we have in our records. If you have recently changed your device or its hardware model, please remove the device (using /remove_device command) and add it back for messaging to work. If you haven't changed the hardware model or reset your device please take it as a warning, some node in the network is using your device id.",
                        _ => throw new NotImplementedException("Unknown security error result")
                    };

                    foreach (var chatId in chatIds)
                    {
                        await TrySendMessage(
                                chatId: chatId,
                                text: msgTxt);
                    }
                }
            }
        }

        private void MaybeSendNotRegisteredResponse(MeshMessage message, Device device)
        {
            if (!botCache.TryRegisterUnknownDeviceResponse(device.DeviceId))
            {
                return;
            }

            var template = _options.Texts.NotRegisteredDeviceReply ??
                "{nodeName} is not registered with {botName} (Telegram). Use /help for more info.";

            var nodeName = StringHelper.Truncate(device.NodeName, 20);
            var botName = _options.TelegramBotUserName;
            var text = template
                .Replace("{nodeName}", nodeName)
                .Replace("{botName}", botName);

            if (!MeshtasticService.CanSendMessage(text))
            {
                throw new InvalidOperationException("Not registered response text is too long to send to Meshtastic device.");
            }

            meshtasticService.SendDirectTextMessage(
                message.DeviceId,
                device.NetworkId,
                device.PublicKey,
                text,
                replyToMessageId: message.Id,
                relayGatewayId: meshSender.GetReplyGatewayId(message),
                hopLimit: message.GetSuggestedReplyHopLimit());
        }

        private async Task<string> FormatTraceRouteMessage(TraceRouteMessage msg)
        {
            var sb = new StringBuilder();

            if (msg.IsTowards)
            {
                sb = await AppendRouteDiscoveryInfo(
                    sb,
                    msg.DeviceId,
                    msg.ToDeviceId,
                    msg.RouteDiscovery.Route,
                    msg.RouteDiscovery.SnrTowards);
            }
            else
            {
                sb = await AppendRouteDiscoveryInfo(
                   sb,
                   msg.ToDeviceId,
                   msg.DeviceId,
                   msg.RouteDiscovery.Route,
                   msg.RouteDiscovery.SnrTowards);

                sb.AppendLine();
                sb.AppendLine("Route back:");

                sb = await AppendRouteDiscoveryInfo(
                   sb,
                   msg.DeviceId,
                   msg.ToDeviceId,
                   msg.RouteDiscovery.RouteBack,
                   msg.RouteDiscovery.SnrBack);
            }

            return sb.ToString();
        }

        private async ValueTask<StringBuilder> AppendRouteDiscoveryInfo(
            StringBuilder sb,
            long fromDeviceId,
            long toDeviceId,
            RepeatedField<uint> route,
            RepeatedField<int> snrSet)
        {
            var fromDeviceName = await GetTraceDeviceName(fromDeviceId);
            sb.AppendLine(fromDeviceName ?? MeshtasticService.GetMeshtasticNodeHexId(fromDeviceId));
            for (int i = 0; i < route.Count; i++)
            {
                var nodeId = route[i];
                var snr = snrSet.Count > i
                    ? snrSet[i]
                    : sbyte.MinValue;

                string deviceName = await GetTraceDeviceName(nodeId);

                sb.AppendLine($"↓↓ SNR {(snr == sbyte.MinValue ? "?" : MeshtasticService.UnroundSnrFromTrace(snr).ToString())} dB");
                sb.AppendLine(deviceName ?? MeshtasticService.GetMeshtasticNodeHexId(nodeId));
            }

            if (snrSet.Count - 1 == route.Count)
            {
                var snr = snrSet.Last();
                sb.AppendLine($"↓↓ SNR {(snr == sbyte.MinValue ? "?" : MeshtasticService.UnroundSnrFromTrace(snr).ToString())} dB");
            }

            var toDeviceName = await GetTraceDeviceName(toDeviceId);
            sb.AppendLine(toDeviceName ?? MeshtasticService.GetMeshtasticNodeHexId(toDeviceId));
            return sb;
        }

        private async ValueTask<string> GetTraceDeviceName(long deviceId)
        {
            string deviceName;
            if (MeshtasticService.IsBroadcastDeviceId(deviceId))
            {
                deviceName = "Unknown " + MeshtasticService.GetMeshtasticNodeHexId(deviceId);
            }
            else if (_options.MeshtasticNodeId == deviceId)
            {
                deviceName = _options.MeshtasticNodeNameLong;
            }
            else
            {
                var device = await registrationService.GetDeviceAsync(deviceId);
                if (device != null)
                {
                    deviceName = device.NodeName;
                }
                else
                {
                    deviceName = null;
                }
            }

            return deviceName;
        }

        private ValueTask<string> GetRecipientName(IRecipient recipient)
        {
            return registrationService.GetRecipientName(recipient);
        }

        private async Task HandleEndChatRequstFromMesh(TextMessage message, Device device, IRecipient recipient)
        {
            var activeSessionTgChatId = await botCache.GetActiveChatSessionForRecipient(recipient, db);

            if (activeSessionTgChatId == null)
            {
                meshtasticService.SendTextMessageToDeviceOrPrivateChannel(
                    recipient,
                    $"No active chat session found",
                    replyToMessageId: message.Id,
                    relayGatewayId: meshSender.GetReplyGatewayId(message),
                    hopLimit: message.GetSuggestedReplyHopLimit());
                return;
            }
            else
            {
                await botCache.StopChatSession(activeSessionTgChatId.Value, db);

                var tgChat = await registrationService.GetTgChatByChatIdAsync(activeSessionTgChatId.Value);
                string chatName = tgChat != null ? tgChat.ChatName : "Unknown";

                meshtasticService.SendTextMessageToDeviceOrPrivateChannel(
                    recipient,
                    $"Chat with {chatName} is ended",
                    replyToMessageId: message.Id,
                    relayGatewayId: meshSender.GetReplyGatewayId(message),
                    hopLimit: message.GetSuggestedReplyHopLimit());

                var recipientName = await GetRecipientName(recipient);
                string tgMsgText;
                if (recipient.RecipientPrivateChannelId.HasValue)
                {
                    var deviceName = device != null
                      ? await GetRecipientName(device)
                      : MeshtasticService.GetMeshtasticNodeHexId(message.DeviceId);

                    tgMsgText = $"❌ Chat with {recipientName} is ended by device {deviceName}";
                }
                else
                {
                    tgMsgText = $"❌ Chat with {recipientName} is ended by device";
                }
                await TrySendMessage(activeSessionTgChatId.Value, tgMsgText);
            }
        }



        private async Task HandleChatRequestFromMesh(TextMessage message, Device fromDevice, IRecipient recipient, string targetHandle)
        {
            var tgChat = await registrationService.GetTgChatByNameAsync(targetHandle);
            bool isPrivateChat = targetHandle.StartsWith('@');
            if (tgChat == null)
            {
                meshtasticService.SendTextMessageToDeviceOrPrivateChannel(
                    recipient,
                    isPrivateChat
                      ? "❌ Telegram user not found or not registered with TMesh."
                      : $"❌ Telegram group not found or not registered with TMesh.",
                    replyToMessageId: message.Id,
                    relayGatewayId: meshSender.GetReplyGatewayId(message),
                    hopLimit: message.GetSuggestedReplyHopLimit());
                return;
            }

            var isApproved = await registrationService.IsApprovedForChatAsync(tgChat.ChatId, recipient);

            if (!tgChat.IsActive && !isApproved)
            {
                meshtasticService.SendTextMessageToDeviceOrPrivateChannel(
                    recipient,
                    $"❌ Chat is disabled. Please reactivate the chat with /start command in Telegram.",
                    replyToMessageId: message.Id,
                    relayGatewayId: meshSender.GetReplyGatewayId(message),
                    hopLimit: message.GetSuggestedReplyHopLimit());
                return;
            }

            var network = await registrationService.GetNetwork(recipient.NetworkId);
            var recipientName = await GetRecipientName(recipient);
            string sentByPart = null;
            if (recipient.RecipientPrivateChannelId != null)
            {
                var deviceName = fromDevice != null
                  ? await GetRecipientName(fromDevice)
                  : MeshtasticService.GetMeshtasticNodeHexId(fromDevice.DeviceId);

                sentByPart = $" by {deviceName}";
            }

            var activeSession = await botCache.GetActiveChatSession(tgChat.ChatId, db);
            if (activeSession != null
                && (activeSession.DeviceId != recipient.RecipientDeviceId
                || activeSession.ChannelId != recipient.RecipientPrivateChannelId
                || activeSession.PublicChannelId != recipient.RecipientPublicChannelId))
            {
                var pendingRequest = new DeviceOrChannelRequestCode
                {
                    Code = RegistrationService.GenerateRandomCode(),
                    DeviceId = recipient.RecipientDeviceId,
                    ChannelId = recipient.RecipientPrivateChannelId
                };
                botCache.StorePendingChatRequest_MeshToTg(tgChat.ChatId, pendingRequest);

                IRecipient otherRecipient = await registrationService.GetRecipientForChatSession(activeSession);

                var otherName = await GetRecipientName(otherRecipient);

                var tgMsg = await TrySendMessage(tgChat.ChatId,
                    $"📥 Chat request from *{StringHelper.EscapeMd(recipientName)}{StringHelper.EscapeMd(sentByPart)}* from {StringHelper.EscapeMd(network?.Name ?? "Unknown Network")}.\n\n" +
                    $"You have active chat session with {StringHelper.EscapeMd(otherName)}\n\n" +
                    $"If you want to end current session and accept new chat request, please reply with the code below.\n\n" +
                    $"Reply with code `{pendingRequest.Code}` to accept the chat request.\n\n" +
                    $"Please ignore request if you don't recognize the device name.\n\n" +
                    $"Accepting this request will allow device to start chats without further approval.",
                    parseMode: ParseMode.Markdown);

                string meshMsgText = tgMsg != null
                    ? $"{targetHandle} already has active chat with someone else. Waiting for approval..."
                    : $"❌ Failed to send request. Please check if the bot is active and has permissions to send messages to {targetHandle}.";

                meshtasticService.SendTextMessageToDeviceOrPrivateChannel(
                       recipient,
                       meshMsgText,
                       replyToMessageId: message.Id,
                       relayGatewayId: meshSender.GetReplyGatewayId(message),
                       hopLimit: message.GetSuggestedReplyHopLimit());

                return;
            }

            if (isApproved)
            {
                var otherSessionTgSessionChatId = await botCache.GetActiveChatSessionForRecipient(recipient, db);
                if (otherSessionTgSessionChatId != null
                    && otherSessionTgSessionChatId != tgChat.ChatId)
                {
                    await botCache.StopChatSession(otherSessionTgSessionChatId.Value, db);
                    await TrySendMessage(
                        otherSessionTgSessionChatId.Value,
                        $"❌ Chat with {recipientName} is ended by device");
                }

                await botCache.StartChatSession(tgChat.ChatId, new DeviceOrChannelId
                {
                    DeviceId = recipient.RecipientDeviceId,
                    ChannelId = recipient.RecipientPrivateChannelId,
                    PublicChannelId = recipient.RecipientPublicChannelId,
                }, db);

                var tgMsg = await TrySendMessage(tgChat.ChatId,
                   $"✅ Chat with {recipientName}{sentByPart} is now active.");

                string meshMsgText = tgMsg != null
                    ? $"✅ Chat is started. You can send messages."
                    : $"❌ Failed to start chat. Please check if the bot is active and has permissions to send messages to {targetHandle}.";

                meshtasticService.SendTextMessageToDeviceOrPrivateChannel(
                       recipient,
                       meshMsgText,
                       replyToMessageId: message.Id,
                       relayGatewayId: meshSender.GetReplyGatewayId(message),
                       hopLimit: message.GetSuggestedReplyHopLimit());
            }
            else
            {
                var pendingRequest = new DeviceOrChannelRequestCode
                {
                    Code = RegistrationService.GenerateRandomCode(),
                    DeviceId = recipient.RecipientDeviceId,
                    ChannelId = recipient.RecipientPrivateChannelId
                };
                botCache.StorePendingChatRequest_MeshToTg(tgChat.ChatId, pendingRequest);

                var tgMsg = await TrySendMessage(tgChat.ChatId,
                    $"📥 Chat request from *{StringHelper.EscapeMd(recipientName)}{StringHelper.EscapeMd(sentByPart)}* from {StringHelper.EscapeMd(network?.Name ?? "Unknown Network")}.\n\n" +
                    $"Reply with code `{pendingRequest.Code}` to accept the chat request.\n\n" +
                    $"Please ignore request if you don't recognize the device name.\n\n" +
                    $"Accepting this request will allow device to start chats without further approval.",
                    parseMode: ParseMode.Markdown);

                var meshMsgText = tgMsg != null
                    ? $"Chat request is sent. Waiting for approval..."
                    : $"❌ Failed to send request. Please check if the bot is active and has permissions to send messages to {targetHandle}.";

                meshtasticService.SendTextMessageToDeviceOrPrivateChannel(
                    recipient,
                    meshMsgText,
                    replyToMessageId: message.Id,
                    relayGatewayId: meshSender.GetReplyGatewayId(message),
                    hopLimit: message.GetSuggestedReplyHopLimit());
            }
        }

        private async Task HandleChatApprovalFromMesh(TextMessage message, Device device, IRecipient recipient, long tgChatId)
        {
            var tgChat = await registrationService.GetTgChatByChatIdAsync(tgChatId);

            var otherMeshSession = await botCache.GetActiveChatSession(tgChatId, db);
            if (otherMeshSession != null
                && (otherMeshSession.DeviceId != recipient.RecipientDeviceId
                || otherMeshSession.ChannelId != recipient.RecipientPrivateChannelId
                || otherMeshSession.PublicChannelId != recipient.RecipientPublicChannelId))
            {
                var chatName = tgChat != null ? tgChat.ChatName : "Telegram";
                await botCache.StopChatSession(tgChatId, db);

                IRecipient other = await registrationService.GetRecipientForChatSession(otherMeshSession);

                if (other != null && !other.IsPublicChannel)
                {
                    var gatewayId = botCache.GetRecipientGateway(recipient);
                    meshtasticService.SendTextMessageToDeviceOrPrivateChannel(
                        other,
                        $"❌ Chat with {chatName} is ended",
                        replyToMessageId: null,
                        relayGatewayId: gatewayId,
                        hopLimit: int.MaxValue);
                }
            }

            var recipientName = await GetRecipientName(recipient);

            var otherSessionTgSessionChatId = await botCache.GetActiveChatSessionForRecipient(recipient, db);
            if (otherSessionTgSessionChatId != null
                && otherSessionTgSessionChatId != tgChatId)
            {
                await botCache.StopChatSession(otherSessionTgSessionChatId.Value, db);
                await botClient.TrySendMessage(
                    registrationService,
                    logger,
                    otherSessionTgSessionChatId.Value,
                    $"❌ Chat with {recipientName} is ended by device");
            }

            botCache.RemovePendingChatRequest_TgToMesh(new DeviceOrChannelId
            {
                DeviceId = recipient.RecipientDeviceId,
                ChannelId = recipient.RecipientPrivateChannelId,
            });

            if (tgChat != null && tgChat.IsActive)
            {
                await registrationService.ApproveDeviceForChatAsync(tgChat.ChatId, message.DeviceId);
            }

            await botCache.StartChatSession(tgChatId, new DeviceOrChannelId
            {
                DeviceId = recipient.RecipientDeviceId,
                ChannelId = recipient.RecipientPrivateChannelId,
                PublicChannelId = recipient.RecipientPublicChannelId,
            }, db);

            string tgMsgText;
            if (recipient.RecipientPrivateChannelId.HasValue)
            {
                var deviceName = device != null
                      ? await GetRecipientName(device)
                      : MeshtasticService.GetMeshtasticNodeHexId(message.DeviceId);

                tgMsgText = $"✅ Chat with {recipientName} was approved by device {deviceName} and is now active.";
            }
            else
            {
                tgMsgText = $"✅ Chat with {recipientName} was approved and is now active.";
            }

            var tgMsg = await TrySendMessage(tgChatId, tgMsgText);

            var meshMsgText = tgMsg != null
                    ? $"✅ Chat approved. You can now send messages."
                    : $"❌ Failed to start chat. Please check if the bot is active and has permissions to send messages to this chat.";

            meshtasticService.SendTextMessageToDeviceOrPrivateChannel(
                recipient,
                meshMsgText,
                replyToMessageId: message.Id,
                relayGatewayId: meshSender.GetReplyGatewayId(message),
                hopLimit: message.GetSuggestedReplyHopLimit());
        }

    }
}
