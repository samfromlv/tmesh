using Google.Protobuf;
using Google.Protobuf.Collections;
using Meshtastic;
using Meshtastic.Crypto;
using Meshtastic.Protobufs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Shared.Models;
using System.Text;
using TBot.Analytics.Models;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
using TBot.Models.MeshMessages;
using TBot.Models.Queue;

namespace TBot
{
    public class MeshtasticService(
        LocalMessageQueueService localMessageQueueService,
        IMemoryCache memoryCache,
        IOptions<TBotOptions> options,
        ILogger<MeshtasticService> logger)
    {
        public const int MaxTextMessageBytes = 233 - MESHTASTIC_PKC_OVERHEAD;
        public const int MaxHops = 7;
        public const int WaitForAckStatusMaxMinutes = 2;
        const int MESHTASTIC_PKC_OVERHEAD = 12;
        private const int NoDupExpirationMinutes = 10;
        private const int LinkTraceExpirationMinutes = 6;
        public const int PkiKeyLength = 32;
        private const int PskKeyLengthShort = 16;
        private const int PskKeyLength = 32;
        private const int ReplyHopsMargin = 2;
        private const int KeepStatsForMinutes = 60;
        public const int MaxLongNodeNameLengthChars = 40;
        private const int MaxChannelNameBytes = 11;
        private const int OkToMqttMask = 1;
        private const int NeedReplyMask = 1 << 1;
        private const int TraceRouteSNRDefault = sbyte.MinValue;
        internal const uint BroadcastDeviceId = uint.MaxValue;
        public const string PKIChannelName = "PKI";
        public const string UnknownChannelName = "UCH";
        private const int MaxMacAddrLengthBytes = 8;
        private static readonly Dictionary<int, LinkedList<MeshStat>> meshStatsByNetwork = [];
        private readonly Dictionary<int, LinkedList<MeshStat>> _meshStatsQueueByNetwork = meshStatsByNetwork;
        private readonly TBotOptions _options = options.Value;

        public QueueResult SendPublicTextMessage(
            long newMessageId,
            string text,
            long? relayGatewayId,
            int hopLimit,
            string publicChannelName,
            IRecipient recipient,
            long? replyToMessageId = null)
        {
            var envelope = PackPublicTextMessage(
                newMessageId,
                text,
                replyToMessageId,
                hopLimit,
                recipient,
                publicChannelName);

            AddStat(new MeshStat
            {
                NetworkId = recipient.NetworkId,
                TextMessagesSent = 1,
            });
            var delay = QueueMessage(envelope, recipient.NetworkId, MessagePriority.Normal, relayGatewayId);
            return new QueueResult
            {
                MessageId = envelope.Packet.Id,
                EstimatedSendDelay = delay
            };
        }

        public QueueResult SendPrivateChannelTextMessage(
            long newMessageId,
            string text,
            long? replyToMessageId,
            long? relayGatewayId,
            int hopLimit,
            IRecipient channel,
            bool isEmoji = false)
        {
            var envelope = PackPrivateTextMessage(
                newMessageId,
                text,
                replyToMessageId,
                hopLimit,
                channel,
                isEmoji);

            AddStat(new MeshStat
            {
                NetworkId = channel.NetworkId,
                TextMessagesSent = 1,
            });
            var delay = QueueMessage(envelope, channel.NetworkId, MessagePriority.Normal, relayGatewayId);
            return new QueueResult
            {
                MessageId = envelope.Packet.Id,
                EstimatedSendDelay = delay
            };
        }

        public QueueResult SendTextMessageToDeviceOrPrivateChannel(
            IRecipient recipient,
            string text,
            long? replyToMessageId,
            long? relayGatewayId,
            int hopLimit,
            string publicChannelName = null)
        {
            if (recipient.RecipientDeviceId.HasValue)
            {
                return SendDirectTextMessage(
                    recipient.RecipientDeviceId.Value,
                    recipient.NetworkId,
                    recipient.RecipientKey,
                    text,
                    replyToMessageId,
                    relayGatewayId,
                    hopLimit);
            }
            else if (recipient.RecipientPrivateChannelId.HasValue)
            {
                return SendPrivateChannelTextMessage(
                    GenerateNewMessageId(),
                    text,
                    replyToMessageId,
                    relayGatewayId,
                    hopLimit,
                    recipient);
            }
            else
            {
                throw new InvalidOperationException("Unknown type of recipient");
            }
        }

        public QueueResult SendDirectTextMessage(
            long deviceId,
            int networkId,
            byte[] publicKey,
            string text,
            long? replyToMessageId,
            long? relayGatewayId,
            int hopLimit)
        {
            return SendDirectTextMessage(GenerateNewMessageId(),
                deviceId,
                networkId,
                publicKey,
                text,
                replyToMessageId,
                relayGatewayId,
                hopLimit);
        }

        public QueueResult SendDirectTextMessage(
            long newMessageId,
            long deviceId,
            int networkId,
            byte[] publicKey,
            string text,
            long? replyToMessageId,
            long? relayGatewayId,
            int hopLimit,
            bool isEmoji = false)
        {
            logger.LogInformation("Sending text message to device {DeviceId}", deviceId);
            var envelope = PackDirectTextMessage(
                newMessageId,
                deviceId,
                publicKey,
                text,
                replyToMessageId,
                hopLimit,
                isEmoji);
            AddStat(new MeshStat
            {
                NetworkId = networkId,
                TextMessagesSent = 1,
            });
            var delay = QueueMessage(envelope, networkId, MessagePriority.Normal, relayGatewayId);
            return new QueueResult
            {
                MessageId = envelope.Packet.Id,
                EstimatedSendDelay = delay
            };
        }

        public static byte? TryGetUsedHops(uint hopStart, uint hopLimit)
        {
            if (hopStart <= 0
                || hopStart > MaxHops
                || hopLimit > hopStart
                || hopLimit < 0)
            {
                return null;
            }
            return (byte)(hopStart - hopLimit);
        }

        public static int GetSuggestedReplyHopLimit(MeshMessage msg)
        {
            var hopsUsed = TryGetUsedHops((uint)msg.HopStart, (uint)msg.HopLimit);
            if (hopsUsed == null)
            {
                return MaxHops;
            }
            var hopsForReply = Math.Max(1, hopsUsed.Value + ReplyHopsMargin);
            return hopsForReply;
        }

        public void AckMeshtasticMessage(
            MeshMessage msg,
            IRecipient recipient,
            long? relayGatewayId)
        {
            if (msg.DeviceId == BroadcastDeviceId)
            {
                throw new InvalidOperationException("No confirmation for broadcast devices");
            }

            var hopsForReply = msg.GetSuggestedReplyHopLimit();
            var envelope = PackAckMessage(msg.DeviceId, msg.Id, hopsForReply, msg.EnvelopeChannelName, recipient);
            AddStat(new MeshStat
            {
                NetworkId = msg.NetworkId,
                AckSent = 1
            });
            QueueMessage(envelope, msg.NetworkId, MessagePriority.High, relayGatewayId);
        }

        public static bool IsBroadcastDeviceId(long deviceId)
        {
            return deviceId == BroadcastDeviceId;
        }

        public void InjectOurNodeInTraceRouteAndSend(
            TraceRouteMessage traceRouteMsg,
            long toDeviceId,
            IRecipient primaryChannel,
            string primartChannelName,
            long incommingGatewayNodeId,
            long outgoingGatewayNodeId)
        {
            var routeDiscovery = traceRouteMsg.RouteDiscovery;

            AddIntermidiateNodeToTraceRoute(traceRouteMsg, incommingGatewayNodeId);
            AddIntermidiateNodeToTraceRoute(traceRouteMsg, _options.MeshtasticNodeId);

            var packet = CreateTraceRoutePacket(
                       toDeviceId,
                       traceRouteMsg.DeviceId,
                       traceRouteMsg.NeedAck,
                       traceRouteMsg.WantsResponse,
                       routeDiscovery,
                       traceRouteMsg.Id,
                       (uint)traceRouteMsg.HopLimit,
                       (uint)traceRouteMsg.HopStart,
                       primaryChannel,
                       traceRouteMsg.RequestId);

            var env = CreateMeshtasticEnvelope(packet, primartChannelName);
            QueueMessage(env, primaryChannel.NetworkId, MessagePriority.High, outgoingGatewayNodeId);
        }

        public void SendTraceRouteRequest(
            long messageId,
            long toDeviceId,
            long? relayGatewayId,
            IRecipient primaryChannel,
            string primaryChannelName)
        {
            var packet = CreateTraceRoutePacket(
                toDeviceId,
                _options.MeshtasticNodeId,
                wantAck: true,
                wantReply: true,
                new RouteDiscovery(),
                messageId,
                (uint)_options.OutgoingMessageHopLimit,
                (uint)_options.OutgoingMessageHopLimit,
                primaryChannel,
                0);

            var envelope = CreateMeshtasticEnvelope(packet, primaryChannelName);

            QueueMessage(envelope, primaryChannel.NetworkId, MessagePriority.Normal, relayGatewayId);
        }

        public void SendTraceRouteToUsResponse(TraceRouteMessage msg,
            long? relayGatewayId,
            IRecipient primaryChannel,
            string primaryChannelName)
        {
            var hopsUsed = msg.HopStart - msg.HopLimit;
            var hopsForReply = msg.GetSuggestedReplyHopLimit();

            var hopStartAndLimit = (uint)Math.Min(_options.OutgoingMessageHopLimit, hopsForReply);

            IRecipient encodeBy;
            string channelName;

            if (msg.DecodedBy == null
                 || (msg.DecodedBy.IsPublicChannel
                    && msg.DecodedBy.RecipientPublicChannelId == primaryChannel.RecipientPublicChannelId))
            {
                encodeBy = primaryChannel;
                channelName = primaryChannelName;
            }
            else
            {
                encodeBy = msg.DecodedBy;
                channelName = UnknownChannelName;
            }

            var packet = CreateTraceRoutePacket(
                msg.DeviceId,
                _options.MeshtasticNodeId,
                msg.NeedAck,
                wantReply: false,
                msg.RouteDiscovery,
                GenerateNewMessageId(),
                hopStartAndLimit,
                hopStartAndLimit,
                encodeBy,
                msg.Id);

            var envelope = CreateMeshtasticEnvelope(packet, channelName);

            AddStat(new MeshStat
            {
                NetworkId = msg.NetworkId,
                TraceRoutes = 1
            });
            QueueMessage(envelope, msg.NetworkId, MessagePriority.Normal, relayGatewayId);
        }

        public void NakNoPubKeyMeshtasticMessage(
            MeshMessage msg,
            long? relayGatewayId,
            IRecipient primaryChannel)
        {
            var hopsForReply = msg.GetSuggestedReplyHopLimit();
            var envelope = PackNoPublicKeyMessage(msg.DeviceId, msg.Id, hopsForReply, msg.EnvelopeChannelName, primaryChannel);
            AddStat(new MeshStat
            {
                NetworkId = msg.NetworkId,
                NakSent = 1
            });
            QueueMessage(envelope, msg.NetworkId, MessagePriority.Low, relayGatewayId);
        }


        public TimeSpan QueueMessage(
            ServiceEnvelope envelope,
            int networkId,
            MessagePriority messagePriority,
            long? relayThroughGatewayId)
        {
            return localMessageQueueService.EnqueueMessage(new QueuedMessage
            {
                Message = envelope,
                NetworkId = networkId,
                RelayThroughGatewayId = relayThroughGatewayId
            }, messagePriority);
        }

        private static string GetNoDupMessageKey(uint id)
        {
            return $"meshtastic:outgoing:{id:X}";
        }

        private ServiceEnvelope PackDirectTextMessage(
            long newMessageId,
            long deviceId,
            byte[] publicKey,
            string text,
            long? replyToMessageId,
            int hopLimit,
            bool isEmoji)
        {
            var packet = CreateTextMessagePacket(
                newMessageId,
                deviceId,
                publicKey,
                text,
                replyToMessageId,
                hopLimit,
                isEmoji);
            var envelope = CreateMeshtasticEnvelope(packet, PKIChannelName);
            return envelope;
        }

        private ServiceEnvelope PackPublicTextMessage(
            long newMessageId,
            string text,
            long? replyToMessageId,
            int hopLimit,
            IRecipient recipient,
            string channelName,
            bool isEmoji = false)
        {
            var packet = CreateTextMessagePacket(
                newMessageId,
                deviceId: BroadcastDeviceId,
                null,
                text,
                replyToMessageId,
                hopLimit,
                isEmoji);

            packet = EncryptPacketWithPsk(packet, recipient);
            var envelope = CreateMeshtasticEnvelope(packet, channelName);
            return envelope;
        }

        private ServiceEnvelope PackPrivateTextMessage(
           long newMessageId,
           string text,
           long? replyToMessageId,
           int hopLimit,
           IRecipient channel,
           bool isEmoji)
        {
            var packet = CreateTextMessagePacket(
                newMessageId,
                deviceId: BroadcastDeviceId,
                null,
                text,
                replyToMessageId,
                hopLimit,
                isEmoji);

            packet = EncryptPacketWithPsk(packet, channel);
            var envelope = CreateMeshtasticEnvelope(packet, UnknownChannelName);
            return envelope;
        }


        private ServiceEnvelope PackAckMessage(
            long deviceId,
            long messageId,
            int messageHopLimit,
            string channelName,
            IRecipient recipient)
        {
            var packet = CreateAckMessagePacket(deviceId, recipient.RecipientType == RecipientType.Device ? recipient.RecipientKey : null, messageId, messageHopLimit);
            if (recipient.RecipientType == RecipientType.Channel)
            {
                packet = EncryptPacketWithPsk(packet, recipient);
            }
            var envelope = CreateMeshtasticEnvelope(packet, channelName);
            return envelope;
        }



        private ServiceEnvelope PackNoPublicKeyMessage(
            long deviceId,
            long messageId,
            int messageHopLimit,
            string channelName,
            IRecipient primaryChannel)
        {
            var packet = CreateNoPublicKeyMessagePacket(deviceId, messageId, messageHopLimit, primaryChannel);
            var envelope = CreateMeshtasticEnvelope(packet, channelName);
            return envelope;
        }

        public ServiceEnvelope CreateMeshtasticEnvelope(MeshPacket packet, string channelName)
        {
            return new ServiceEnvelope()
            {
                Packet = packet,
                ChannelId = channelName ?? PKIChannelName,
                GatewayId = GetMeshtasticNodeHexId(_options.MeshtasticNodeId),
            };
        }

        public static string GetMeshtasticNodeHexId(long deviceId)
        {
            return $"!{deviceId:x8}";
        }



        private MeshPacket CreateTextMessagePacket(
            long newMessageId,
            long deviceId,
            byte[] publicKey,
            string text,
            long? replyToMessageId,
            int hopLimit,
            bool isEmoji)
        {
            var bytes = ByteString.CopyFromUtf8(text);
            if (bytes.Length > MaxTextMessageBytes)
                throw new ArgumentException($"Message too long for Meshtastic ({bytes.Length} bytes, max {MaxTextMessageBytes})");

            var hopLimitToUse = Math.Max(Math.Min(hopLimit, _options.OutgoingMessageHopLimit), 0);

            var packet = new MeshPacket()
            {
                Channel = 0,
                WantAck = true,
                To = (uint)deviceId,
                From = (uint)_options.MeshtasticNodeId,
                Priority = MeshPacket.Types.Priority.Default,
                Id = (uint)newMessageId,
                HopLimit = (uint)hopLimitToUse,
                HopStart = (uint)hopLimitToUse,
                Decoded = new Data()
                {
                    Bitfield = OkToMqttMask,
                    ReplyId = replyToMessageId.HasValue ? (uint)replyToMessageId.Value : 0,
                    Portnum = PortNum.TextMessageApp,
                    Payload = bytes,
                    Emoji = isEmoji ? 1u : 0u
                },
            };

            if (publicKey != null && publicKey.Length > 0)
            {
                Meshtastic.Crypto.PKIEncryption.Encrypt(
                    Convert.FromBase64String(_options.MeshtasticPrivateKeyBase64),
                    publicKey,
                    packet
                );

                packet.PkiEncrypted = true;
                // Not needed, as it's in the encrypted payload.
                // The proxy node should have this node's public key for retransmission.
                // packet.PublicKey = ByteString.FromBase64(_options.MeshtasticPublicKeyBase64);
            }

            return packet;
        }

        private static uint GenerateNewMessageId()
        {
            return (uint)Math.Floor(Random.Shared.Next() * 1e9);
        }

        public static long GetNextMeshtasticMessageId()
        {
            return GenerateNewMessageId();
        }

        public MeshPacket CreateAckMessagePacket(
            long deviceId,
            byte[] publicKey,
            long messageId,
            int messageHopLimit)
        {
            var routeData = new Routing
            {
                ErrorReason = Routing.Types.Error.None
            };

            var packet = new MeshPacket()
            {
                Channel = 0,
                WantAck = false,
                To = (uint)deviceId,
                From = (uint)_options.MeshtasticNodeId,
                Priority = MeshPacket.Types.Priority.Ack,
                Id = GenerateNewMessageId(),
                HopLimit = (uint)Math.Min(_options.OutgoingMessageHopLimit, messageHopLimit),
                HopStart = (uint)Math.Min(_options.OutgoingMessageHopLimit, messageHopLimit),
                Decoded = new Meshtastic.Protobufs.Data()
                {
                    Portnum = PortNum.RoutingApp,
                    RequestId = (uint)messageId,
                    Bitfield = OkToMqttMask,
                    Payload = routeData.ToByteString(),
                },
            };

            if (publicKey != null && publicKey.Length > 0)
            {
                Meshtastic.Crypto.PKIEncryption.Encrypt(
                    Convert.FromBase64String(_options.MeshtasticPrivateKeyBase64),
                    publicKey,
                    packet
                );

                packet.PkiEncrypted = true;
                // Not needed, as it's in the encrypted payload.
                // The proxy node should have this node's public key for retransmission.
                // packet.PublicKey = ByteString.FromBase64(_options.MeshtasticPublicKeyBase64);
            }

            return packet;
        }

        public MeshPacket CreateTraceRoutePacket(
           long deviceId,
           long fromDeviceId,
           bool wantAck,
           bool wantReply,
           RouteDiscovery routeDiscovery,
           long messageId,
           uint hopLimit,
           uint hopStart,
           IRecipient primaryChannel,
           long requestId)
        {
            var packet = new MeshPacket()
            {
                Channel = 0,
                WantAck = wantAck,
                To = (uint)deviceId,
                From = (uint)fromDeviceId,
                Priority = MeshPacket.Types.Priority.Reliable,
                Id = (uint)messageId,
                HopLimit = hopLimit,
                HopStart = hopStart,
                Decoded = new Meshtastic.Protobufs.Data()
                {
                    WantResponse = wantReply,
                    Portnum = PortNum.TracerouteApp,
                    RequestId = (uint)requestId,
                    Bitfield = OkToMqttMask,
                    Payload = routeDiscovery.ToByteString(),
                },
            };
            if (wantReply)
            {
                packet.Decoded.Bitfield |= NeedReplyMask;
            }

            packet = EncryptPacketWithPsk(packet, primaryChannel);
            return packet;
        }

        public MeshPacket CreateNoPublicKeyMessagePacket(
          long deviceId,
          long messageId,
          int messageHopLimit,
          IRecipient primaryChannel)
        {
            var routeData = new Routing
            {
                ErrorReason = Routing.Types.Error.PkiUnknownPubkey
            };

            var hopLimit = (uint)Math.Min(_options.OutgoingMessageHopLimit, messageHopLimit);

            var packet = new MeshPacket()
            {
                Channel = 0,
                WantAck = false,
                To = (uint)deviceId,
                From = (uint)_options.MeshtasticNodeId,
                Priority = MeshPacket.Types.Priority.Ack,
                Id = GenerateNewMessageId(),
                HopLimit = hopLimit,
                HopStart = hopLimit,
                Decoded = new Data()
                {
                    Portnum = PortNum.RoutingApp,
                    RequestId = (uint)messageId,
                    Bitfield = OkToMqttMask,
                    Payload = routeData.ToByteString(),
                },
            };

            packet = EncryptPacketWithPsk(packet, primaryChannel);

            return packet;
        }

        public static bool IsValidChannelName(string name)
        {
            return !string.IsNullOrEmpty(name)
                && Encoding.UTF8.GetByteCount(name) <= MaxChannelNameBytes;
        }

        public static bool IsValidChannelKey(string key)
        {
            return TryParseChannelKey(key, out _);
        }

        public static string PskKeyToBase64(byte[] key)
        {
            if (key.SequenceEqual(Resources.DEFAULT_PSK))
            {
                return Convert.ToBase64String(new byte[] { 1 });
            }

            return Convert.ToBase64String(key);
        }

        public static bool IsDefaultKey(byte[] key)
        {
            return key != null && key.SequenceEqual(Resources.DEFAULT_PSK);
        }

        public static bool TryParseChannelKey(string key, out byte[] buffer)
        {
            if (string.IsNullOrEmpty(key))
            {
                buffer = null;
                return false;
            }

            if (key.Length > PskKeyLength * 2)
            {
                buffer = null;
                return false;
            }

            //check if it's base64 and decodes to the correct length
            var bufferLong = new byte[PskKeyLength];
            if (!Convert.TryFromBase64String(key, bufferLong, out var bytesWritten))
            {
                buffer = null;
                return false;
            }
            if (bytesWritten == 1 && bufferLong[0] == 1)
            {
                buffer = Resources.DEFAULT_PSK;
                return true;
            }

            if (bytesWritten != PskKeyLength && bytesWritten != PskKeyLengthShort)
            {
                buffer = null;
                return false;
            }
            buffer = new byte[bytesWritten];
            Array.Copy(bufferLong, buffer, bytesWritten);
            return true;
        }


        public static bool TryParseDeviceId(string input, out long deviceId)
        {
            if (string.IsNullOrEmpty(input))
            {
                deviceId = 0;
                return false;
            }

            input = input.Trim();
            if (input.StartsWith("!", StringComparison.OrdinalIgnoreCase)
                || input.StartsWith("#", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(input.AsSpan(1),
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out deviceId))
                    return true;
            }
            if (long.TryParse(input, out deviceId))
            {
                return true;
            }
            deviceId = 0;
            return false;
        }

        public static long PraseDeviceHexId(string input) =>
            long.Parse(input.AsSpan(1),
                   System.Globalization.NumberStyles.HexNumber,
                   null);

        public static (string publicKeyBase64, string privateKeyBase64) GenerateKeyPair()
        {
            var (privateKey, publicKey) = Meshtastic.Crypto.PKIEncryption.GenerateKeyPair();
            return (Convert.ToBase64String(publicKey), Convert.ToBase64String(privateKey));
        }

        public static bool CanSendMessage(string text)
        {
            var byteCount = Encoding.UTF8.GetByteCount(text);
            return byteCount <= MaxTextMessageBytes;
        }

        public void SendVirtualNodeInfo(
            string primaryChannelName,
            IRecipient primaryChannel,
            int hopLimit,
            long destinationDeviceId = BroadcastDeviceId,
            long? relayThroughGatewayId = null)
        {
            var packet = CreateTMeshVirtualNodeInfo(primaryChannel, hopLimit, destinationDeviceId);
            var envelope = CreateMeshtasticEnvelope(packet, primaryChannelName);
            var id = envelope.Packet.Id;
            StoreNoDup(id);
            QueueMessage(
                envelope,
                primaryChannel.NetworkId,
                destinationDeviceId == BroadcastDeviceId
                    ? MessagePriority.Low
                    : MessagePriority.High,
                relayThroughGatewayId);
        }

        public void StoreNoDup(uint id)
        {
            memoryCache.Set(GetNoDupMessageKey(id), true, TimeSpan.FromMinutes(NoDupExpirationMinutes));
        }

        public bool TryStoreNoDup(ServiceEnvelope env)
        {
            if (env?.Packet == null)
            {
                return false;
            }
            var id = env.Packet.Id;
            return TryStoreNoDup(id);
        }

        public bool TryStoreNoDup(uint id)
        {
            var key = GetNoDupMessageKey(id);
            if (memoryCache.TryGetValue(key, out _))
            {
                return false;
            }
            memoryCache.Set(key, true, TimeSpan.FromMinutes(NoDupExpirationMinutes));
            return true;
        }



        public void MarkAsLinkTrace(long packetId)
        {
            var key = $"meshtastic:linktrace:{packetId:X}";
            memoryCache.Set(key, true, TimeSpan.FromMinutes(LinkTraceExpirationMinutes));
        }

        public bool IsPreviouslySeenLinkTrace(ServiceEnvelope env)
        {
            var key = $"meshtastic:linktrace:{env.Packet.Id:X}";
            return memoryCache.TryGetValue(key, out _);
        }

        public void MarkAsNodeInfo(long packetId)
        {
            var key = $"meshtastic:nodeinfo:{packetId:X}";
            memoryCache.Set(key, true, TimeSpan.FromMinutes(LinkTraceExpirationMinutes));
        }

        public bool IsPreviouslySeenNodeInfo(ServiceEnvelope env)
        {
            var key = $"meshtastic:nodeinfo:{env.Packet.Id:X}";
            return memoryCache.TryGetValue(key, out _);
        }

        public void MarkUplinkPacket(long packetId)
        {
            var key = $"meshtastic:uplinkpacket:{packetId:X}";
            memoryCache.Set(key, true, TimeSpan.FromMinutes(NoDupExpirationMinutes));
        }

        public bool IsUplinkPacket(ServiceEnvelope env)
        {
            var key = $"meshtastic:uplinkpacket:{env.Packet.Id:X}";
            return memoryCache.TryGetValue(key, out _);
        }

        public bool TryStoreLinkTraceGatewayNoDup(long packetId, long gatewayId)
        {
            var key = $"meshtastic:linktracegw:{packetId:X}:{gatewayId:X}";
            if (memoryCache.TryGetValue(key, out _))
            {
                return false;
            }
            memoryCache.Set(key, true, TimeSpan.FromMinutes(NoDupExpirationMinutes));
            return true;
        }

        private byte[] GetMacAddressFromNodeId()
        {
            var mac = new byte[6];
            mac[0] = 0x32;//Private/local MAC address range
            mac[1] = 0x57;
            var nodeId = (uint)_options.MeshtasticNodeId;
            for (int i = 5; i >= 2; i--)
            {
                mac[i] = (byte)(nodeId & 0xFF);
                nodeId >>= 8;
            }
            return mac;
        }

        private MeshPacket CreateTMeshVirtualNodeInfo(
            IRecipient primaryChannel,
            int hopLimit,
            long destinationDeviceId = BroadcastDeviceId)
        {
            hopLimit = Math.Max(Math.Min(hopLimit, _options.OwnNodeInfoMessageHopLimit), 0);

            var packet = new MeshPacket()
            {
                Channel = 0,
                WantAck = false,
                To = (uint)destinationDeviceId,
                From = (uint)_options.MeshtasticNodeId,
                Priority = MeshPacket.Types.Priority.Background,
                Id = GenerateNewMessageId(),
                HopLimit = (uint)hopLimit,
                HopStart = (uint)hopLimit,
                Decoded = new Data()
                {
                    Bitfield = OkToMqttMask,
                    Portnum = PortNum.NodeinfoApp,
                    Payload = new User
                    {
                        HwModel = HardwareModel.DiyV1,
                        Id = GetMeshtasticNodeHexId(_options.MeshtasticNodeId),
                        IsLicensed = false,
                        LongName = _options.MeshtasticNodeNameLong,
                        IsUnmessagable = false, // Field name from external library kept as-is.
                        ShortName = _options.MeshtasticNodeNameShort,
                        Role = Config.Types.DeviceConfig.Types.Role.ClientHidden,
                        PublicKey = ByteString.FromBase64(_options.MeshtasticPublicKeyBase64)
                    }.ToByteString(),
                },
            };

            packet = EncryptPacketWithPsk(packet, primaryChannel);
            return packet;
        }

        private static MeshPacket EncryptPacketWithPsk(MeshPacket packet, IRecipient channel)
        {
            return EncryptPacketWithPsk(packet, channel.RecipientChannelXor.Value, channel.RecipientKey);
        }

        private static MeshPacket EncryptPacketWithPsk(MeshPacket packet, byte channelXorHash, byte[] channelPsk)
        {
            var input = packet.Decoded.ToByteArray();
            var nonce = new Meshtastic.Crypto.NonceGenerator(packet.From, packet.Id);
            var encrypted = TransformPacket(input, nonce.Create(), channelPsk, true);
            packet.Encrypted = ByteString.CopyFrom(encrypted);
            packet.Channel = channelXorHash;
            return packet;
        }

        // XOR-based hash for byte arrays
        private static byte XorHash(byte[] data)
        {
            byte result = 0;
            for (int i = 0; i < data.Length; i++)
                result ^= data[i];
            return result;
        }

        public static byte GenerateChannelHash(string name, byte[] key)
        {
            if (string.IsNullOrEmpty(name) || key == null || key.Length == 0)
                return 0;

            byte h = XorHash(Encoding.UTF8.GetBytes(name));
            h ^= XorHash(key);

            return h;
        }

        public static (Data, IRecipient) DecryptPacketWithPsk(MeshPacket packet, IEnumerable<IRecipient> recipients)
        {
            var input = packet.Encrypted.ToByteArray();
            var nonce = new NonceGenerator(packet.From, packet.Id);

            foreach (var channel in recipients.Where(x => x.RecipientChannelXor == (byte)packet.Channel))
            {
                var decrypted = TransformPacket(input, nonce.Create(), channel.RecipientKey, false);
                try
                {
                    var decoded = Data.Parser.ParseFrom(decrypted);
                    if (decoded.Portnum != PortNum.UnknownApp)
                    {
                        return (decoded, channel);
                    }
                }
                catch
                {
                    continue;
                }
            }
            return default;
        }

        public static byte[] TransformPacket(byte[] input, byte[] nonce, byte[] key, bool forEncryption)
        {
            // Create a new cipher instance for each call
            var cipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");

            // Initialize for encryption or decryption
            cipher.Init(forEncryption, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", key), nonce));

            // Perform the transformation
            return cipher.DoFinal(input);
        }



        public static MeshAddressInfo GetPacketAddresses(
            ServiceEnvelope envelope)
        {
            if (envelope?.Packet == null)
            {
                return null;
            }
            return new MeshAddressInfo
            {
                From = envelope.Packet.From,
                To = envelope.Packet.To,
                IsPkiEncrypted = envelope.Packet.PkiEncrypted,
                XorHash = (byte)envelope.Packet.Channel
            };
        }

        internal static bool OkToMqtt(Data decoded)
        {
            return decoded == null
                || !decoded.HasBitfield
                || (decoded.Bitfield & OkToMqttMask) != 0;
        }

        public (bool success, MeshMessage msg) TryDecryptMessage(
            ServiceEnvelope envelope,
            List<IRecipient> recipients,
            int networkId,
            bool isTMeshGateway)
        {
            if (envelope?.Packet == null)
            {
                return default;
            }
            if (!TryParseDeviceId(envelope.GatewayId, out var gatewayNodeId))
            {
                gatewayNodeId = 0;
            }

            if (gatewayNodeId == _options.MeshtasticNodeId)
            {
                return default;
            }

            if (envelope.Packet.PayloadVariantCase != MeshPacket.PayloadVariantOneofCase.Encrypted)
            {
                return default;
            }

            var id = envelope.Packet.Id;

            if (!envelope.Packet.PkiEncrypted)
            {
                return DecryptPksMessage(envelope, recipients, gatewayNodeId, networkId, isTMeshGateway);
            }

            if (envelope.Packet.To != _options.MeshtasticNodeId)
                return default;

            var device = recipients.FirstOrDefault(x => x.RecipientDeviceId != null);

            var publicKey = device?.RecipientKey;

            if (publicKey == null)
            {
                var msg = MeshMessage.FromEnvelope<EncryptedDirectMessage>(envelope, null, null, networkId, isTMeshGateway);
                return (true, msg);
            }

            (var pkiOk, var decodedPki) = DecryptPKI(
                Convert.FromBase64String(_options.MeshtasticPrivateKeyBase64),
                publicKey,
                envelope.Packet);

            if (!pkiOk)
            {
                return default;
            }
            if (decodedPki.Portnum == PortNum.UnknownApp)
            {
                var msg = MeshMessage.FromEnvelope<EncryptedDirectMessage>(envelope, null, null, networkId, isTMeshGateway);
                return (true, msg);
            }
            else if (decodedPki.Portnum == PortNum.TextMessageApp)
            {
                var msg = MeshMessage.FromEnvelope<TextMessage>(envelope, decodedPki, device, networkId, isTMeshGateway);

                msg.IsDirectMessage = true;
                msg.Text = decodedPki.Payload.ToStringUtf8();
                msg.IsEmoji = decodedPki.Emoji != 0;
                msg.ReplyTo = decodedPki.ReplyId;
                AddStat(new MeshStat
                {
                    NetworkId = device.NetworkId,
                    DirectTextMessagesRecieved = 1,
                });

                return (true, msg);
            }
            else if (decodedPki.Portnum == PortNum.RoutingApp)
            {
                var ack = DecodeAck(envelope, decodedPki, device, networkId, isTMeshGateway);
                if (ack == null)
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decodedPki, device, networkId, isTMeshGateway));
                }
                AddStat(new MeshStat
                {
                    NetworkId = device.NetworkId,
                    AckRecieved = ack.Success ? 1 : 0,
                    NakRecieved = ack.Success ? 0 : 1
                });
                return (true, ack);
            }
            else if (decodedPki.Portnum == PortNum.NodeinfoApp)
            {
                var res = DecodeNodeInfo(envelope, decodedPki, device, networkId, isTMeshGateway);
                if (!res.success)
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decodedPki, device, networkId, isTMeshGateway));
                }
                return res;
            }
            return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decodedPki, device, networkId, isTMeshGateway));
        }

        public (bool success, MeshMessage msg) TryDecryptPskTraceRoute(
           ServiceEnvelope envelope,
           IRecipient recipient,
           int networkId,
           bool isTMeshGateway)
        {
            if (envelope.Packet.PkiEncrypted)
            {
                return default;
            }

            var (decoded, _) = DecryptPacketWithPsk(envelope.Packet, [recipient]);
            if (decoded == null || decoded.Portnum != PortNum.TracerouteApp)
            {
                return default;
            }

            return ReadTraceRoute(envelope, decoded, recipient, networkId, isTMeshGateway);
        }

        private (bool success, MeshMessage msg) DecryptPksMessage(
            ServiceEnvelope envelope,
            List<IRecipient> recipients,
            long gatewayNodeId,
            int networkId,
            bool isTMeshGateway)
        {
            if (recipients == null || recipients.Count == 0)
            {
                return default;
            }

            var (decoded, recipient) = DecryptPacketWithPsk(envelope.Packet, recipients);
            if (decoded == null)
            {
                return default;
            }

            if (decoded.Portnum == PortNum.NodeinfoApp)
            {
                var res = DecodeNodeInfo(envelope, decoded, recipient, networkId, isTMeshGateway);
                if (!res.success)
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient, networkId, isTMeshGateway));
                }
                return res;
            }
            else if (decoded.Portnum == PortNum.RoutingApp
                && envelope.Packet.To == _options.MeshtasticNodeId)
            {
                AddStat(new MeshStat
                {
                    NetworkId = recipient.NetworkId,
                    AckRecieved = 1,
                });
                var ack = DecodeAck(envelope, decoded, recipient, networkId, isTMeshGateway);
                if (ack == null)
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient, networkId, isTMeshGateway));
                }
                return (true, ack);
            }
            else if (decoded.Portnum == PortNum.TracerouteApp
                && envelope.Packet.To == _options.MeshtasticNodeId)
            {
                var res = ReadTraceRoute(envelope, decoded, recipient, networkId, isTMeshGateway);
                if (res.success && res.msg is TraceRouteMessage trs)
                {
                    AddIntermidiateNodeToTraceRoute(trs, gatewayNodeId);
                    if (trs.IsTowards)
                    {
                        trs.RouteDiscovery.SnrTowards.Add(TraceRouteSNRDefault);
                    }
                    else
                    {
                        trs.RouteDiscovery.SnrBack.Add(TraceRouteSNRDefault);
                    }
                }
                return res;
            }
            else if (decoded.Portnum == PortNum.PositionApp)
            {
                if (envelope.Packet.To != _options.MeshtasticNodeId
                    && envelope.Packet.To != BroadcastDeviceId)
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient, networkId, isTMeshGateway));
                }

                Position position;
                try
                {
                    position = Position.Parser.ParseFrom(decoded.Payload);
                }
                catch
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient, networkId, isTMeshGateway));
                }

                if (position == null
                    || !position.HasLatitudeI
                    || !position.HasLongitudeI
                    || position.PrecisionBits < 0
                    || position.PrecisionBits > 32)
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient, networkId, isTMeshGateway));
                }

                var accuracyMeters = MeshtasticPositionUtils.PrecisionBitsToAccuracyMeters((int)position.PrecisionBits);

                if (position.HDOP != 0)
                {
                    var hdopAccuracy = MeshtasticPositionUtils.DopToAccuracyMeters(position.HDOP);
                    if (hdopAccuracy > accuracyMeters)
                    {
                        accuracyMeters = hdopAccuracy;
                    }
                }
                else if (position.PDOP != 0)
                {
                    var pdopAccuracy = MeshtasticPositionUtils.DopToAccuracyMeters(position.PDOP);
                    if (pdopAccuracy > accuracyMeters)
                    {
                        accuracyMeters = pdopAccuracy;
                    }
                }

                var msg = MeshMessage.FromEnvelope<PositionMessage>(envelope, decoded, recipient, networkId, isTMeshGateway);
                msg.Latitude = position.LatitudeI / 1e7;
                msg.Longitude = position.LongitudeI / 1e7;
                msg.HeadingDegrees = position.HasGroundTrack
                    ? MeshtasticPositionUtils.GroundTrackToHeading((int)position.GroundTrack)
                    : null;
                msg.Altitude = position.HasAltitude
                        ? position.Altitude
                        : null;
                msg.AccuracyMeters = accuracyMeters;
                msg.SentToOurNodeId = envelope.Packet.To == _options.MeshtasticNodeId;
                return (true, msg);
            }
            else if (decoded.Portnum == PortNum.TextMessageApp)
            {
                var msg = MeshMessage.FromEnvelope<TextMessage>(envelope, decoded, recipient, networkId, isTMeshGateway);
                msg.Text = decoded.Payload.ToStringUtf8();
                msg.IsEmoji = decoded.Emoji != 0;
                msg.IsDirectMessage = false;
                msg.ReplyTo = decoded.ReplyId;
                AddStat(new MeshStat
                {
                    NetworkId = recipient.NetworkId,
                    PublicTextMessagesRecieved = recipient.IsPublicChannel ? 1 : 0,
                    PrivateChannelTextMessagesRecieved = recipient.IsPublicChannel ? 0 : 1
                });

                return (true, msg);
            }
            else if (decoded.Portnum == PortNum.TelemetryApp)
            {
                Telemetry telemetry;
                try
                {
                    telemetry = Telemetry.Parser.ParseFrom(decoded.Payload);
                }
                catch
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient, networkId, isTMeshGateway));
                }

                if (telemetry == null
                    || telemetry.DeviceMetrics == null
                    || (!telemetry.DeviceMetrics.HasChannelUtilization
                    && !telemetry.DeviceMetrics.HasAirUtilTx))
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient, networkId, isTMeshGateway));
                }

                var msg = MeshMessage.FromEnvelope<DeviceMetricsMessage>(envelope, decoded, recipient, networkId, isTMeshGateway);
                msg.ChannelUtilization = telemetry.DeviceMetrics.HasChannelUtilization
                        ? telemetry.DeviceMetrics.ChannelUtilization
                        : null;
                msg.AirUtilization = telemetry.DeviceMetrics.HasAirUtilTx
                        ? telemetry.DeviceMetrics.AirUtilTx
                        : null;

                return (true, msg);
            }
            else
            {
                return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient, networkId, isTMeshGateway));
            }
        }


        private static void AddIntermidiateNodeToTraceRoute(
            TraceRouteMessage trs,
            long nodeId)
        {
            var isTowards = trs.RequestId == 0;
            RepeatedField<uint> route;
            RepeatedField<int> snr;
            if (isTowards)
            {
                route = trs.RouteDiscovery.Route;
                snr = trs.RouteDiscovery.SnrTowards;
            }
            else
            {
                route = trs.RouteDiscovery.RouteBack;
                snr = trs.RouteDiscovery.SnrBack;
            }


            var hopsUsed = trs.HopStart - trs.HopLimit;

            while (route.Count < hopsUsed)
            {
                route.Add(BroadcastDeviceId);
                snr.Add(TraceRouteSNRDefault);
            }

            if (!route.Contains((uint)nodeId)
                        && trs.DeviceId != nodeId
                        && trs.ToDeviceId != nodeId)
            {
                route.Add((uint)nodeId);
                snr.Add(trs.RxSnrRounded);
            }
        }

        private static (bool success, MeshMessage msg) ReadTraceRoute(
            ServiceEnvelope envelope,
            Data decoded,
            IRecipient recipient,
            int networkId,
            bool isTMeshGateway)
        {
            RouteDiscovery routeDiscovery;
            try
            {
                routeDiscovery = RouteDiscovery.Parser.ParseFrom(decoded.Payload);
            }
            catch
            {
                return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient, networkId, isTMeshGateway));
            }


            var msg = MeshMessage.FromEnvelope<TraceRouteMessage>(envelope, decoded, recipient, networkId, isTMeshGateway);
            msg.RouteDiscovery = routeDiscovery;
            msg.RequestId = decoded.RequestId;
            msg.WantsResponse = (decoded.Bitfield & NeedReplyMask) != 0;
            msg.RxSnrRounded = RoundSnrForTrace(envelope.Packet.RxSnr);
            msg.ToDeviceId = envelope.Packet.To;
            return (true, msg);
        }

        public static bool PacketWantsResponse(MeshPacket packet)
        {
            if (packet?.Decoded == null)
            {
                return false;
            }
            return (packet.Decoded.Bitfield & NeedReplyMask) != 0;
        }

        public static (bool success, Data data) DecryptPKI(byte[] recipientPrivateKey, byte[] senderPublicKey, MeshPacket meshPacket)
        {
            if (meshPacket.Encrypted == null || meshPacket.Encrypted.Length == 0) return (false, null);
            var encryptedData = meshPacket.Encrypted.ToByteArray();
            var decrypted = PKIEncryption.Decrypt(recipientPrivateKey, senderPublicKey, encryptedData, meshPacket.Id, meshPacket.From);
            Data data;
            try
            {
                data = Data.Parser.ParseFrom(decrypted);
            }
            catch
            {
                return (false, null);
            }
            return (true, data);
        }

        private static int RoundSnrForTrace(float snr)
        {
            return (int)Math.Round(snr * 4);
        }

        public static float UnroundSnrFromTrace(int snr)
        {
            return snr / 4.0f;
        }

        public TimeSpan EstimateDelay(int networkId, MessagePriority priority)
        {
            return localMessageQueueService.EstimateDelay(networkId, priority);
        }

        public int GetQueueLength(int networkId) => localMessageQueueService.GetQueueLength(networkId);

        public TimeSpan SingleMessageQueueDelay => localMessageQueueService.SingleMessageQueueDelay;

        private static AckMessage DecodeAck(
            ServiceEnvelope envelope,
            Data decoded, 
            IRecipient recipient, 
            int networkId,
            bool isTMeshGateway)
        {
            Routing routing;
            try
            {
                routing = Routing.Parser.ParseFrom(decoded.Payload);
            }
            catch
            {
                return null;
            }

            var msg = MeshMessage.FromEnvelope<AckMessage>(envelope, decoded, recipient, networkId, isTMeshGateway);
            msg.NeedAck = false;
            msg.AckedMessageId = decoded.RequestId;
            msg.IsPkiEncrypted = envelope.Packet.PkiEncrypted;
            msg.Success = routing.ErrorReason == Routing.Types.Error.None;
            msg.Error = routing.ErrorReason;
            return msg;
        }

        private (bool success, MeshMessage msg) DecodeNodeInfo(
            ServiceEnvelope envelope,
            Data decoded, 
            IRecipient recipient,
            int networkId,
            bool isTMeshGateway)
        {
            User user;
            try
            {
                user = User.Parser.ParseFrom(decoded.Payload);
            }
            catch
            {
                return default;
            }
            if (user?.PublicKey == null
                || user.PublicKey.Length != PkiKeyLength
                || user.PublicKey.All(x => x == 0)
                || (user.LongName != null && user.LongName.Length > MaxLongNodeNameLengthChars))
            {
                return default;
            }

            long deviceId = envelope.Packet.From;
            if (TryParseDeviceId(user.Id, out var parsedId))
            {
                deviceId = parsedId;
            }
            AddStat(new MeshStat
            {
                NetworkId = recipient.NetworkId,
                NodeInfoRecieved = 1,
            });
            var msg = MeshMessage.FromEnvelope<NodeInfoMessage>(envelope, decoded, recipient, networkId, isTMeshGateway);
            msg.DeviceId = deviceId;
            msg.NodeName = user.LongName ?? user.ShortName ?? user.Id;
            msg.PublicKey = user.PublicKey.ToByteArray();

            msg.Packet = new Packet
            {
                GatewayId = (uint)msg.GatewayId,
                IsTMeshGateway = msg.TMeshGatewayId.HasValue,
                Channel = (byte)envelope.Packet.Channel,
                DecodedByPublicChannelId = recipient.RecipientPublicChannelId,
                Dest = decoded.Dest,
                From = envelope.Packet.From,
                HopLimit = (byte)envelope.Packet.HopLimit,
                HopStart = (byte)envelope.Packet.HopStart,
                IsEmoji = decoded.Emoji != 0,
                MqttChannel = envelope.ChannelId,
                NeedReplyFlag = decoded.HasBitfield && (decoded.Bitfield & NeedReplyMask) != 0,
                NextHop = (byte)envelope.Packet.NextHop,
                OkToMqttFlag = decoded.HasBitfield && (decoded.Bitfield & OkToMqttMask) != 0,
                PacketId = envelope.Packet.Id,
                PkiEncrypted = recipient.RecipientDeviceId != null,
                PortNum = (int)decoded.Portnum,
                Priority = (byte)envelope.Packet.Priority,
                RelayNode = (byte)envelope.Packet.RelayNode,
                ReplyId = decoded.ReplyId,
                RequestId = decoded.RequestId,
                RxRssi = envelope.Packet.RxRssi,
                RxSnr = envelope.Packet.RxSnr,
                RxTimestamp = envelope.Packet.RxTime,
                Source = decoded.Source,
                To = envelope.Packet.To,
                Transport = (byte)envelope.Packet.TransportMechanism,
                TxAfter = envelope.Packet.TxAfter,
                ViaMqtt = envelope.Packet.ViaMqtt,
                WantAck = envelope.Packet.WantAck,
                WantResponse = decoded.WantResponse,
            };

            msg.NodeInfo = new Analytics.Models.NodeInfo
            {
                UserId = user.Id,
                PublicKey = user.PublicKey?.ToByteArray(),
                ShortName = user.ShortName,
                LongName = user.LongName,
                HardwareModel = user.HwModel != HardwareModel.Unset ? (int?)user.HwModel : null,
                IsLicensed = user.IsLicensed,
                IsUnmessagable = user.IsUnmessagable,
                Role = (byte)user.Role,
                MacAddr = user.Macaddr != null
                        && user.Macaddr.Length > 0
                        && user.Macaddr.Length <= MaxMacAddrLengthBytes
                    ? MacBytesToUInt64(user.Macaddr)
                    : null
            };

            return (true, msg);
        }

        private static long MacBytesToUInt64(IEnumerable<byte> bytes)
        {
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));

            long value = 0;
            foreach (byte b in bytes)
            {
                value = (value << 8) | b;
            }
            return value;
        }


        public MeshStat AggregateStartFrom(int networkId, DateTime fromUtc)
        {
            var aggregate = new MeshStat()
            {
                NetworkId = networkId,
                IntervalStart = fromUtc
            };
            lock (_meshStatsQueueByNetwork)
            {
                if (!_meshStatsQueueByNetwork.TryGetValue(networkId, out var queue))
                {
                    return aggregate;
                }

                foreach (var stat in queue)
                {
                    if (stat.IntervalStart >= fromUtc)
                    {
                        aggregate.Add(stat);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            return aggregate;
        }

        public void AddStat(MeshStat stat)
        {
            lock (_meshStatsQueueByNetwork)
            {
                var roundedUtcNow = DateTime.UtcNow;
                roundedUtcNow = new DateTime(
                    roundedUtcNow.Year,
                    roundedUtcNow.Month,
                    roundedUtcNow.Day,
                    roundedUtcNow.Hour,
                    roundedUtcNow.Minute,
                    0,
                    DateTimeKind.Utc);

                if (!_meshStatsQueueByNetwork.TryGetValue(stat.NetworkId, out var queue))
                {
                    queue = new LinkedList<MeshStat>();
                    _meshStatsQueueByNetwork[stat.NetworkId] = queue;
                }

                var existingStat = queue.First?.Value;

                if (existingStat != null)
                {
                    if (roundedUtcNow == existingStat.IntervalStart)
                    {
                        // Same interval, aggregate stats
                        existingStat.Add(stat);
                        return;
                    }
                }
                stat.IntervalStart = roundedUtcNow;
                queue.AddFirst(stat);
                var border = roundedUtcNow.AddMinutes(-KeepStatsForMinutes);
                while (queue.Count > 0 && queue.Last.Value.IntervalStart < border)
                {
                    queue.RemoveLast();
                }
            }
        }

    }
}
