using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using System.Text;
using TBot.Analytics;
using TBot.Analytics.Models;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
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
        IServiceProvider services)
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
            if (message.NeedAck)
            {
                var primaryChannel = await registrationService.GetNetworkPrimaryChannelCached(message.NetworkId);
                if (primaryChannel != null)
                {
                    meshtasticService.AckMeshtasticMessage(message, primaryChannel, message.GatewayId);
                }
                else
                {
                    logger.LogWarning("Received encrypted direct message for network {NetworkId} without primary channel, cannot send ack", message.NetworkId);
                }
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

            meshtasticService.SendTextMessage(
                message.DeviceId,
                deviceOrNull.NetworkId,
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
                var replyToStatus = botCache.GetMeshMessageStatus(message.ReplyTo);
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

            if (meshtasticService.GetQueueLength(channel.NetworkId) < _options.MaxQueueLengthForChannelAckEmojis)
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

            if (cmdText != null &&
                _options.PingWords.Any(pingWord => string.Equals(cmdText, pingWord, StringComparison.OrdinalIgnoreCase)))
            {
                meshtasticService.SendTextMessage(
                    message.DeviceId,
                    deviceOrNull.NetworkId,
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
                var replyToStatus = botCache.GetMeshMessageStatus(message.ReplyTo);
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

                    meshSender.StoreTelegramMessageStatus(deviceOrNull.NetworkId, replyToStatus.TelegramChatId,
                        msg.Id,
                        status);

                    botCache.StoreMeshMessageStatus(deviceOrNull.NetworkId, message.Id, status);

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

            meshtasticService.SendTraceRouteResponse(message, message.GatewayId, primaryChannel);
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
            var res = await registrationService.SaveDeviceAsync(
                message.DeviceId,
                message.NetworkId,
                message.NodeName,
                message.PublicKey);

            if (res.device != null && res.device.PublicKey != null)
            {
                meshtasticService.AckMeshtasticMessage(
                  message,
                  res.device,
                  message.GatewayId);
            }

            if (!res.success)
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
                    await botClient.SendMessage(
                        chatId,
                        $"Warning: The new public key was detected for device {device.NodeName}. If you have recently reset your device or changed encryption keys, please remove the device (using /remove_device command) and add it back for messaging to work. Public keys are not updated automaticly after device first registration due security reasons. If you haven't changed the keys or reset your device please take it as a warning, some node in the network is using your device id.");
                }
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
                device.NetworkId,
                device.PublicKey,
                text,
                replyToMessageId: null,
                relayGatewayId: message.GatewayId,
                hopLimit: message.GetSuggestedReplyHopLimit());
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

    }
}
