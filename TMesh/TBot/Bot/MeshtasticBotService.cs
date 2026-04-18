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
        TBotDbContext db)
    {
        private readonly TBotOptions _options = options.Value;

        public List<MeshtasticMessageStatus> TrackedMessages => meshSender.TrackedMessages;


        public async Task ProcessInboundMeshtasticMessage(MeshMessage message, Device deviceOrNull)
        {
            botCache.StoreDeviceGateway(message);
            if (message.ChannelId.HasValue && message.IsSingleDeviceChannel)
            {
                botCache.StoreChannelGateway((int)message.ChannelId.Value, message.GatewayId, message.DeviceId, message.GetSuggestedReplyHopLimit());
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
                    await SendNoPublicKeyNak(message);
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

        private async Task SendNoPublicKeyNak(MeshMessage message)
        {
            var primaryChannel = await registrationService.GetNetworkPrimaryChannelCached(message.NetworkId);
            if (primaryChannel != null)
            {
                meshtasticService.NakNoPubKeyMeshtasticMessage(message, message.GatewayId, primaryChannel);
            }
            else
            {
                logger.LogWarning("Received encrypted direct message for network {NetworkId} without primary channel, cannot send ack", message.NetworkId);
            }
        }

        private async Task ProcessInboundDeviceMetricsMessage(DeviceMetricsMessage message, Device deviceOrNull)
        {
            if (message.NeedAck)
            {
                deviceOrNull ??= await registrationService.GetDeviceAsync(message.DeviceId);
                if (deviceOrNull != null)
                {
                    meshtasticService.AckMeshtasticMessage(message, deviceOrNull, message.GatewayId);
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

            deviceOrNull ??= await registrationService.GetDeviceAsync(message.DeviceId);
            if (deviceOrNull?.LocationUpdatedUtc == null)
            {
                return;
            }
            logger.LogDebug("Processing inbound Meshtastic message: {Message}", message);

            var metrics = new DeviceMetric
            {
                NetworkId = deviceOrNull.NetworkId,
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
            deviceOrNull ??= await registrationService.GetDeviceAsync(message.DeviceId);
            if (deviceOrNull == null)
            {
                return;
            }
            if (message.NeedAck)
            {
                meshtasticService.AckMeshtasticMessage(message, deviceOrNull, message.GatewayId);
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

            var chatIds = await GetChatsForDevice(message.DeviceId);

            if (chatIds.Count == 0)
            {
                MaybeSendNotRegisteredResponse(message, deviceOrNull);
                return;
            }

            foreach (var chatId in chatIds)
            {
                var msg = await TrySendMessage(
                    chatId,
                    $"{deviceOrNull.NodeName} sent a location:");
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

            var activeSessionChatId = botCache.GetActiveChatSessionForDevice(deviceId);
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
                await SendNoPublicKeyNak(message);
                return;
            }

            var network = await registrationService.GetNetwork(deviceOrNull.NetworkId);

            if (!network.DisablePongs)
            {
                meshtasticService.SendDirectTextMessage(
                    message.DeviceId,
                    deviceOrNull.NetworkId,
                    deviceOrNull.PublicKey,
                    GetPingReplyText(network),
                    replyToMessageId: null,//Message is from public channel and we are sending direct reply, so no replyToMessageId
                    relayGatewayId: message.GatewayId,
                    hopLimit: message.GetSuggestedReplyHopLimit());

                meshtasticService.AddStat(new Shared.Models.MeshStat
                {
                    NetworkId = deviceOrNull.NetworkId,
                    PongSent = 1,
                });
            }
        }

        private string GetPingReplyText(Network network)
        {
            var reply = network?.Url == null ?
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

            meshtasticService.SendTextMessage(
                recipient,
                helpText,
                replyToMessageId: msg.Id,
                relayGatewayId: msg.GatewayId,
                hopLimit: msg.GetSuggestedReplyHopLimit());
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

            // Handle /chat @username command from Mesh device
            if (cmdText != null && cmdText.StartsWith("chat ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = cmdText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var targetHandle = parts[1];
                    await HandleChatRequestFromMesh(message, deviceOrNull, channel, targetHandle);
                    return;
                }
            }
            else if (cmdText != null && cmdText.StartsWith("end_chat", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEndChatRequstFromMesh(message, deviceOrNull, channel);
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
                        await HandleChatApprovalFromMesh(message, deviceOrNull, channel, pendingRequest.ChatId);
                        return;
                    }
                    else
                    {
                        meshtasticService.SendTextMessage(
                            channel,
                            $"Invalid code. To approve reply with code {pendingRequest.Code}.",
                            replyToMessageId: message.Id,
                            relayGatewayId: message.GatewayId,
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
                    relayGatewayId: message.GatewayId,
                    hopLimit: message.GetSuggestedReplyHopLimit(),
                    channel);

                meshtasticService.AddStat(new Shared.Models.MeshStat
                {
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

            var activeChatId = botCache.GetActiveChatSessionForChannel((int)message.ChannelId.Value);
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
                                        Type = RecipientType.Channel,
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
                                        Type = RecipientType.Channel,
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

            if (!message.IsEmoji && meshtasticService.GetQueueLength(channel.NetworkId) < _options.MaxQueueLengthForChannelAckEmojis)
            {
                meshtasticService.SendPrivateChannelTextMessage(
                    MeshtasticService.GetNextMeshtasticMessageId(),
                    "✓",
                    message.Id,
                    relayGatewayId: message.GatewayId,
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

        private async Task ProcessInboundDirectMeshTextMessage(TextMessage message, Device deviceOrNull)
        {
            if (deviceOrNull == null)
            {
                throw new ArgumentNullException(nameof(deviceOrNull), "Device cannot be null for Text messages");
            }
            if (message.NeedAck)
            {
                meshtasticService.AckMeshtasticMessage(
                    message,
                    deviceOrNull,
                    message.GatewayId);
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
                    await HandleChatRequestFromMesh(message, deviceOrNull, deviceOrNull, targetHandle);
                    return;
                }
                else
                {
                    meshtasticService.SendDirectTextMessage(
                        message.DeviceId,
                        deviceOrNull.NetworkId,
                        deviceOrNull.PublicKey,
                        $"Use /chat @<tg_username> or /chat <tg_group_name>",
                        replyToMessageId: message.Id,
                        relayGatewayId: message.GatewayId,
                        hopLimit: message.GetSuggestedReplyHopLimit());
                }
            }
            else if (cmdText != null && cmdText.StartsWith("end_chat", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEndChatRequstFromMesh(message, deviceOrNull, deviceOrNull);
                return;
            }
            else if (cmdText != null && cmdText.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                HandleHelpCommand(message, deviceOrNull);
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
                        await HandleChatApprovalFromMesh(message, deviceOrNull, deviceOrNull, pendingRequest.ChatId);
                        return;
                    }
                    else
                    {
                        meshtasticService.SendDirectTextMessage(
                            message.DeviceId,
                            deviceOrNull.NetworkId,
                            deviceOrNull.PublicKey,
                            $"Invalid code. To approve reply with code {pendingRequest.Code}, 'no' to reject.",
                            replyToMessageId: message.Id,
                            relayGatewayId: message.GatewayId,
                            hopLimit: message.GetSuggestedReplyHopLimit());
                        return;
                    }
                }
            }

            if (cmdText != null &&
                _options.PingWords.Any(pingWord => string.Equals(cmdText, pingWord, StringComparison.OrdinalIgnoreCase)))
            {
                var network = await registrationService.GetNetwork(deviceOrNull.NetworkId);

                meshtasticService.SendDirectTextMessage(
                    message.DeviceId,
                    deviceOrNull.NetworkId,
                    deviceOrNull.PublicKey,
                    GetPingReplyText(network),
                    replyToMessageId: message.Id,
                    relayGatewayId: message.GatewayId,
                    hopLimit: message.GetSuggestedReplyHopLimit());

                meshtasticService.AddStat(new Shared.Models.MeshStat
                {
                    PongSent = 1,
                });
                return;
            }

            var chatIds = await GetChatsForDevice(message.DeviceId);
            if (chatIds.Count == 0)
            {
                if (message.ReplyTo == 0)
                {
                    MaybeSendNotRegisteredResponse(message, deviceOrNull);
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
                        text: $"{deviceOrNull.NodeName}: {text}",
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

                    meshSender.StoreTelegramMessageStatus(deviceOrNull.NetworkId, replyToStatus.TelegramChatId,
                        msg.Id,
                        status);

                    botCache.StoreMeshMessageStatus(deviceOrNull.NetworkId, message.Id, status);

                    sentReply = true;
                }
            }

            if (!sentReply)
            {
                if (chatIds.Count == 0)
                {
                    MaybeSendNotRegisteredResponse(message, deviceOrNull);
                    return;
                }

                foreach (var chatId in chatIds)
                {
                    var msg = await TrySendMessage(
                        chatId: chatId,
                        text: $"{deviceOrNull.NodeName}: {text}");

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

                    meshSender.StoreTelegramMessageStatus(deviceOrNull.NetworkId, chatId, msg.Id, status);
                    botCache.StoreMeshMessageStatus(deviceOrNull.NetworkId, message.Id, status);
                }
            }
        }


        private async Task ProcessInboundTraceRoute(TraceRouteMessage message, Device deviceOrNull)
        {
            var primaryChannel = await registrationService.GetNetworkPrimaryChannelCached(message.NetworkId);
            if (primaryChannel == null)
            {
                logger.LogWarning("Received trace route message for network {NetworkId} without primary channel, ignoring", message.NetworkId);
                return;
            }

            if (message.IsTowards)
            {
                await ProcessTowardsTrace(message, deviceOrNull, primaryChannel);
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

        private async Task ProcessTowardsTrace(TraceRouteMessage message, Device deviceOrNull, PublicChannel primaryChannel)
        {
            if (message.WantsResponse)
            {
                meshtasticService.SendTraceRouteToUsResponse(message, message.GatewayId, primaryChannel, primaryChannel.Name);
            }
            deviceOrNull ??= await registrationService.GetDeviceAsync(message.DeviceId);
            if (deviceOrNull == null)
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
                message.PublicKey);

            if (message.NeedAck && res.device != null && res.device.PublicKey != null)
            {
                meshtasticService.AckMeshtasticMessage(
                  message,
                  res.device,
                  message.GatewayId);
            }

            if (res.res == SaveResult.Inserted)
            {
                var network = await registrationService.GetNetwork(message.NetworkId);
                if (network != null 
                      && !network.DisableWelcomeMessage
                      && (!string.IsNullOrEmpty(network.Url) 
                            || !string.IsNullOrEmpty(network.CommunityUrl)))
                {
                    var template = _options.Texts.NewDeviceWelcomeMessage_Template ?? "Welcome!{settings}{community}";
                    var settingsPart = _options.Texts.NewDeviceWelcomeMessage_Settings ?? " Settings: {url}.";
                    var communityPart = _options.Texts.NewDeviceWelcomeMessage_Community ?? " Community: {url}";

                    var msgText = new StringBuilder(template);
                    msgText = msgText
                      .Replace("{settings}", string.IsNullOrEmpty(network.Url) ? "" : settingsPart.Replace("{url}", network.Url))
                      .Replace("{community}", string.IsNullOrEmpty(network.CommunityUrl) ? "" : communityPart.Replace("{url}", network.CommunityUrl));  

                    var primaryChannel = await registrationService.GetNetworkPrimaryChannelCached(message.NetworkId);
                    if (primaryChannel == null)
                    {
                        return;
                    }


                    meshtasticService.SendVirtualNodeInfo(
                         primaryChannel.Name,
                         primaryChannel,
                         message.GetSuggestedReplyHopLimit(),
                         destinationDeviceId: message.DeviceId,
                         message.GatewayId);

                    meshtasticService.SendDirectTextMessage(
                        message.DeviceId,
                        message.NetworkId,
                        message.PublicKey,
                        msgText.ToString(),
                        replyToMessageId: null,
                        relayGatewayId: message.GatewayId,
                        hopLimit: message.GetSuggestedReplyHopLimit());
                }
            }
            else if (res.res == SaveResult.SecurityError)
            {
                var device = res.device;
                if (device == null)
                {
                    return;
                }

                //Node public key do not match our records, delivery of messages to this node will not work
                //we need to warn users that his public key was changed and he need to remove device and readded it
                var chatIds = await registrationService.GetChatsByDeviceIdCached(message.DeviceId);
                foreach (var chatId in chatIds)
                {
                    await TrySendMessage(
                        chatId: chatId,
                        text: $"Warning: The new public key was detected for device {device.NodeName}. If you have recently reset your device or changed encryption keys, please remove the device (using /remove_device command) and add it back for messaging to work. Public keys are not updated automatically after device first registration due security reasons. If you haven't changed the keys or reset your device please take it as a warning, some node in the network is using your device id.");
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
                relayGatewayId: message.GatewayId,
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
            var activeSessionTgChatId = botCache.GetActiveChatSessionForRecipient(recipient);

            if (activeSessionTgChatId == null)
            {
                meshtasticService.SendTextMessage(
                    recipient,
                    $"No active chat session found",
                    replyToMessageId: message.Id,
                    relayGatewayId: message.GatewayId,
                    hopLimit: message.GetSuggestedReplyHopLimit());
                return;
            }
            else
            {
                await botCache.StopChatSession(activeSessionTgChatId.Value, db);

                var tgChat = await registrationService.GetTgChatByChatIdAsync(activeSessionTgChatId.Value);
                string chatName = tgChat != null ? tgChat.ChatName : "Unknown";

                meshtasticService.SendTextMessage(
                    recipient,
                    $"Chat with {chatName} is ended",
                    replyToMessageId: message.Id,
                    relayGatewayId: message.GatewayId,
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
                meshtasticService.SendTextMessage(
                    recipient,
                    isPrivateChat
                      ? "❌ Telegram user not found or not registered with TMesh."
                      : $"❌ Telegram group not found or not registered with TMesh.",
                    replyToMessageId: message.Id,
                    relayGatewayId: message.GatewayId,
                    hopLimit: message.GetSuggestedReplyHopLimit());
                return;
            }

            var isApproved = await registrationService.IsApprovedForChatAsync(tgChat.ChatId, recipient);

            if (!tgChat.IsActive && !isApproved)
            {
                meshtasticService.SendTextMessage(
                    recipient,
                    $"❌ Chat is disabled. Please reactivate the chat with /start command in Telegram.",
                    replyToMessageId: message.Id,
                    relayGatewayId: message.GatewayId,
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

            var activeSession = botCache.GetActiveChatSession(tgChat.ChatId);
            if (activeSession != null
                && (activeSession.DeviceId != recipient.RecipientDeviceId
                || activeSession.ChannelId != recipient.RecipientPrivateChannelId))
            {
                var pendingRequest = new DeviceOrChannelRequestCode
                {
                    Code = RegistrationService.GenerateRandomCode(),
                    DeviceId = recipient.RecipientDeviceId,
                    ChannelId = recipient.RecipientPrivateChannelId
                };
                botCache.StorePendingChatRequest_MeshToTg(tgChat.ChatId, pendingRequest);

                IRecipient otherRecipient = activeSession.DeviceId != null
                   ? await registrationService.GetDeviceAsync(activeSession.DeviceId.Value)
                   : await registrationService.GetChannelAsync(activeSession.ChannelId.Value);

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

                meshtasticService.SendTextMessage(
                       recipient,
                       meshMsgText,
                       replyToMessageId: message.Id,
                       relayGatewayId: message.GatewayId,
                       hopLimit: message.GetSuggestedReplyHopLimit());

                return;
            }

            if (isApproved)
            {
                var otherSessionTgSessionChatId = botCache.GetActiveChatSessionForRecipient(recipient);
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
                }, db);

                var tgMsg = await TrySendMessage(tgChat.ChatId,
                   $"✅ Chat with {recipientName}{sentByPart} is now active.");

                string meshMsgText = tgMsg != null
                    ? $"✅ Chat is started. You can send messages."
                    : $"❌ Failed to start chat. Please check if the bot is active and has permissions to send messages to {targetHandle}.";

                meshtasticService.SendTextMessage(
                       recipient,
                       meshMsgText,
                       replyToMessageId: message.Id,
                       relayGatewayId: message.GatewayId,
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

                meshtasticService.SendTextMessage(
                    recipient,
                    meshMsgText,
                    replyToMessageId: message.Id,
                    relayGatewayId: message.GatewayId,
                    hopLimit: message.GetSuggestedReplyHopLimit());
            }
        }

        private async Task HandleChatApprovalFromMesh(TextMessage message, Device device, IRecipient recipient, long tgChatId)
        {
            var tgChat = await registrationService.GetTgChatByChatIdAsync(tgChatId);

            var otherMeshSession = botCache.GetActiveChatSession(tgChatId);
            if (otherMeshSession != null
                && (otherMeshSession.DeviceId != recipient.RecipientDeviceId
                || otherMeshSession.ChannelId != recipient.RecipientPrivateChannelId))
            {
                var chatName = tgChat != null ? tgChat.ChatName : "Telegram";
                await botCache.StopChatSession(tgChatId, db);

                IRecipient other = otherMeshSession.DeviceId != null
                   ? await registrationService.GetDeviceAsync(otherMeshSession.DeviceId.Value)
                   : await registrationService.GetChannelAsync(otherMeshSession.ChannelId.Value);

                if (other != null)
                {
                    var gatewayId = botCache.GetRecipientGateway(recipient);
                    meshtasticService.SendTextMessage(
                        other,
                        $"❌ Chat with {chatName} is ended",
                        replyToMessageId: null,
                        relayGatewayId: gatewayId,
                        hopLimit: int.MaxValue);
                }
            }

            var recipientName = await GetRecipientName(recipient);

            var otherSessionTgSessionChatId = botCache.GetActiveChatSessionForRecipient(recipient);
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
                DeviceId = message.DeviceId,
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

            meshtasticService.SendTextMessage(
                recipient,
                meshMsgText,
                replyToMessageId: message.Id,
                relayGatewayId: message.GatewayId,
                hopLimit: message.GetSuggestedReplyHopLimit());
        }

    }
}
