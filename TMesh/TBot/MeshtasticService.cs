using Google.Protobuf;
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
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
using TBot.Models.MeshMessages;
using TBot.Models.Queue;

namespace TBot
{
    public class MeshtasticService
    {
        public const int MaxTextMessageBytes = 233 - MESHTASTIC_PKC_OVERHEAD;
        public const int MaxHops = 7;
        public const int WaitForAckStatusMaxMinutes = 2;
        const int MESHTASTIC_PKC_OVERHEAD = 12;
        private const int NoDupExpirationMinutes = 3;
        private const int LinkTraceExpirationMinutes = 6;
        private const int PkiKeyLength = 32;
        private const int PskKeyLengthShort = 16;
        private const int PskKeyLength = 32;
        private const int ReplyHopsMargin = 2;
        private const int KeepStatsForMinutes = 60;
        private const int MaxChannelNameBytes = 11;
        private const int OkToMqttMask = 1;
        private const int NeedReplyMask = 1 << 1;
        private const int TraceRouteSNRDefault = sbyte.MinValue;
        internal const uint BroadcastDeviceId = uint.MaxValue;
        public const string PKIChannelName = "PKI";
        public const string UnknownChannelName = "UCH";
        private static readonly Dictionary<int, LinkedList<MeshStat>> meshStatsByNetwork = new();
        private readonly Dictionary<int, LinkedList<MeshStat>> _meshStatsQueueByNetwork = meshStatsByNetwork;

        public MeshtasticService(
            MqttService mqttService,
            LocalMessageQueueService localMessageQueueService,
            IMemoryCache memoryCache,
            IOptions<TBotOptions> options,
            ILogger<MeshtasticService> logger)
        {
            _mqttService = mqttService;
            _localMessageQueueService = localMessageQueueService;
            _logger = logger;
            _memoryCache = memoryCache;
            _options = options.Value;
        }


        private readonly MqttService _mqttService;
        private readonly ILogger<MeshtasticService> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly TBotOptions _options;
        private readonly LocalMessageQueueService _localMessageQueueService;


        public QueueResult SendPublicTextMessage(
            string text,
            long? relayGatewayId,
            int hopLimit,
            string publicChannelName,
            IRecipient recipient)
        {
            var envelope = PackPublicTextMessage(
                GenerateNewMessageId(),
                text,
                null,
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
        public QueueResult SendTextMessage(
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
            _logger.LogInformation("Sending message to device {DeviceId}: {Message}", deviceId, text);
            var envelope = PackTextMessage(
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
            var envelope = PackAckMessage(msg.DeviceId, msg.Id, hopsForReply, recipient);
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

        public void SendTraceRouteResponse(TraceRouteMessage msg,
            long? relayGatewayId,
            IRecipient primaryChannel)
        {
            var hopsUsed = msg.HopStart - msg.HopLimit;
            var hopsForReply = msg.GetSuggestedReplyHopLimit();
            var envelope = PackTraceRouteResponse(
                msg.DeviceId,
                msg.RouteDiscovery,
                msg.Id,
                hopsUsed,
                hopsForReply,
                primaryChannel);

            AddStat(new MeshStat
            {
                NetworkId = msg.NetworkId,
                TraceRoutes = 1
            });
            QueueMessage(envelope, msg.NetworkId, MessagePriority.Low, relayGatewayId);
        }

        public void NakNoPubKeyMeshtasticMessage(
            MeshMessage msg,
            long? relayGatewayId,
            IRecipient primaryChannel)
        {
            var hopsForReply = msg.GetSuggestedReplyHopLimit();
            var envelope = PackNoPublicKeyMessage(msg.DeviceId, msg.Id, hopsForReply, primaryChannel);
            AddStat(new MeshStat
            {
                NetworkId = msg.NetworkId,
                NakSent = 1
            });
            QueueMessage(envelope, msg.NetworkId, MessagePriority.Low, relayGatewayId);
        }


        private TimeSpan QueueMessage(
            ServiceEnvelope envelope,
            int networkId,
            MessagePriority messagePriority,
            long? relayThroughGatewayId)
        {
            return _localMessageQueueService.EnqueueMessage(new QueuedMessage
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

        private ServiceEnvelope PackTextMessage(
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
            IRecipient recipient)
        {
            var packet = CreateAckMessagePacket(deviceId, recipient.RecipientType == RecipientType.Device ? recipient.RecipientKey : null, messageId, messageHopLimit);
            if (recipient.RecipientType == RecipientType.Channel)
            {
                packet = EncryptPacketWithPsk(packet, recipient);
            }
            var envelope = CreateMeshtasticEnvelope(packet, PKIChannelName);
            return envelope;
        }

        private ServiceEnvelope PackTraceRouteResponse(
            long deviceId,
            RouteDiscovery routeDiscovery,
            long messageId,
            int hopsUsed,
            int messageHopLimit,
            IRecipient primaryChannel)
        {
            var packet = CreateTraceRouteResponsePacket(
                deviceId,
                routeDiscovery,
                messageId,
                hopsUsed,
                messageHopLimit,
                primaryChannel);
            var envelope = CreateMeshtasticEnvelope(packet, PKIChannelName);
            return envelope;
        }

        private ServiceEnvelope PackNoPublicKeyMessage(
            long deviceId,
            long messageId,
            int messageHopLimit,
            IRecipient primaryChannel)
        {
            var packet = CreateNoPublicKeyMessagePacket(deviceId, messageId, messageHopLimit, primaryChannel);
            var envelope = CreateMeshtasticEnvelope(packet, PKIChannelName);
            return envelope;
        }

        private ServiceEnvelope CreateMeshtasticEnvelope(MeshPacket packet, string channelName)
        {
            return new ServiceEnvelope()
            {
                Packet = packet,
                ChannelId = channelName,
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

        public MeshPacket CreateTraceRouteResponsePacket(
           long deviceId,
           RouteDiscovery routeDiscovery,
           long messageId,
           int hopsUsed,
           int messageHopLimit,
           IRecipient primaryChannel)
        {
            while (routeDiscovery.Route.Count < hopsUsed)
            {
                routeDiscovery.Route.Add(BroadcastDeviceId);
                routeDiscovery.SnrTowards.Add(sbyte.MinValue);
            }

            routeDiscovery.SnrTowards.Add(TraceRouteSNRDefault);

            var packet = new MeshPacket()
            {
                Channel = 0,
                WantAck = false,
                To = (uint)deviceId,
                From = (uint)_options.MeshtasticNodeId,
                Priority = MeshPacket.Types.Priority.Reliable,
                Id = GenerateNewMessageId(),
                HopLimit = (uint)Math.Min(_options.OutgoingMessageHopLimit, messageHopLimit),
                HopStart = (uint)Math.Min(_options.OutgoingMessageHopLimit, messageHopLimit),
                Decoded = new Meshtastic.Protobufs.Data()
                {
                    Portnum = PortNum.TracerouteApp,
                    RequestId = (uint)messageId,
                    Bitfield = OkToMqttMask,
                    Payload = routeDiscovery.ToByteString(),
                },
            };
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

        public void SendVirtualNodeInfo(string primaryChannelName, IRecipient primaryChannel)
        {
            var packet = CreateTMeshVirtualNodeInfo(primaryChannel);
            var envelope = CreateMeshtasticEnvelope(packet, primaryChannelName);
            var id = envelope.Packet.Id;
            StoreNoDup(id);
            _localMessageQueueService.EnqueueMessage(new QueuedMessage
            {
                NetworkId = primaryChannel.NetworkId,
                Message = envelope
            }, MessagePriority.Low);
        }

        public void StoreNoDup(uint id)
        {
            _memoryCache.Set(GetNoDupMessageKey(id), true, TimeSpan.FromMinutes(NoDupExpirationMinutes));
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
            if (_memoryCache.TryGetValue(key, out _))
            {
                return false;
            }
            _memoryCache.Set(key, true, TimeSpan.FromMinutes(NoDupExpirationMinutes));
            return true;
        }



        public void StoreGatewayLinkTraceStepZero(long packetId)
        {
            var key = $"meshtastic:linktrace:{packetId:X}";
            _memoryCache.Set(key, true, TimeSpan.FromMinutes(LinkTraceExpirationMinutes));
        }

        public bool IsLinkTrace(ServiceEnvelope env)
        {
            var key = $"meshtastic:linktrace:{env.Packet.Id:X}";
            return _memoryCache.TryGetValue(key, out _);
        }

        public void MarkUplinkPacket(long packetId)
        {
            var key = $"meshtastic:uplinkpacket:{packetId:X}";
            _memoryCache.Set(key, true, TimeSpan.FromMinutes(NoDupExpirationMinutes));
        }

        public bool IsUplinkPacket(ServiceEnvelope env)
        {
            var key = $"meshtastic:uplinkpacket:{env.Packet.Id:X}";
            return _memoryCache.TryGetValue(key, out _);
        }

        public bool TryStoreLinkTraceGatewayNoDup(long packetId, long gatewayId)
        {
            var key = $"meshtastic:linktracegw:{packetId:X}:{gatewayId:X}";
            if (_memoryCache.TryGetValue(key, out _))
            {
                return false;
            }
            _memoryCache.Set(key, true, TimeSpan.FromMinutes(NoDupExpirationMinutes));
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

        private MeshPacket CreateTMeshVirtualNodeInfo(IRecipient primaryChannel)
        {
            var packet = new MeshPacket()
            {
                Channel = 0,
                WantAck = false,
                To = BroadcastDeviceId,
                From = (uint)_options.MeshtasticNodeId,
                Priority = MeshPacket.Types.Priority.Background,
                Id = GenerateNewMessageId(),
                HopLimit = (uint)_options.OwnNodeInfoMessageHopLimit,
                HopStart = (uint)_options.OwnNodeInfoMessageHopLimit,
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
                var decoded = Data.Parser.ParseFrom(decrypted);
                if (decoded.Portnum != PortNum.UnknownApp)
                {
                    return (decoded, channel);
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

        public bool TryBridge(ServiceEnvelope envelope, Dictionary<long, int> gatewayIds)
        {
            if (envelope.Packet == null)
            {
                return false;
            }

            var senderDeviceId = envelope.Packet.From;
            var receiverDeviceId = envelope.Packet.To;

            if (_options.BridgeDirectMessagesToGateways
                   && senderDeviceId != receiverDeviceId
                   && senderDeviceId != _options.MeshtasticNodeId
                   && gatewayIds.TryGetValue(receiverDeviceId, out var receiverNetworkId)
                   && envelope.GatewayId != GetMeshtasticNodeHexId(_options.MeshtasticNodeId))
            {
                var newMsg = envelope.Clone();
                newMsg.GatewayId = GetMeshtasticNodeHexId(_options.MeshtasticNodeId);
                IncreaseBridgeDirectMessagesToGatewaysStat(receiverNetworkId);
                QueueMessage(newMsg, receiverNetworkId, MessagePriority.High, receiverDeviceId);
                return true;
            }
            return false;
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
            List<IRecipient> recipients)
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
                return DecryptPksMessage(envelope, recipients, gatewayNodeId);
            }

            if (envelope.Packet.To != _options.MeshtasticNodeId)
                return default;

            var device = recipients.FirstOrDefault(x => x.RecipientDeviceId != null);

            var publicKey = device?.RecipientKey;

            if (publicKey == null)
            {
                var msg = MeshMessage.FromEnvelope<EncryptedDirectMessage>(envelope, null, null);
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
                var msg = MeshMessage.FromEnvelope<EncryptedDirectMessage>(envelope, null, null);
                return (true, msg);
            }
            else if (decodedPki.Portnum == PortNum.TextMessageApp)
            {
                var msg = MeshMessage.FromEnvelope<TextMessage>(envelope, decodedPki, device);

                msg.IsDirectMessage = true;
                msg.Text = decodedPki.Payload.ToStringUtf8();
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
                AddStat(new MeshStat
                {
                    NetworkId = device.NetworkId,
                    AckRecieved = 1,
                });
                return (true, DecodeAck(envelope, decodedPki, device));
            }
            else if (decodedPki.Portnum == PortNum.NodeinfoApp)
            {
                var res = DecodeNodeInfo(envelope, decodedPki, device);
                if (!res.success)
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decodedPki, device));
                }
                return res;
            }
            return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decodedPki, device));
        }

        private (bool success, MeshMessage msg) DecryptPksMessage(ServiceEnvelope envelope, List<IRecipient> recipients, long gatewayNodeId)
        {
            var (decoded, recipient) = DecryptPacketWithPsk(envelope.Packet, recipients);
            if (decoded == null)
            {
                return default;
            }

            if (decoded.Portnum == PortNum.NodeinfoApp)
            {
                var res = DecodeNodeInfo(envelope, decoded, recipient);
                if (!res.success)
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient));
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
                return (true, DecodeAck(envelope, decoded, recipient));
            }
            else if (decoded.Portnum == PortNum.TracerouteApp
                && envelope.Packet.To == _options.MeshtasticNodeId)
            {
                var routeDiscovery = RouteDiscovery.Parser.ParseFrom(decoded.Payload);

                if (!routeDiscovery.Route.Contains((uint)gatewayNodeId)
                    && envelope.Packet.From != gatewayNodeId)
                {
                    routeDiscovery.Route.Add((uint)gatewayNodeId);
                    routeDiscovery.SnrTowards.Add(RoundSnrForTrace(envelope.Packet.RxSnr));
                }
                var msg = MeshMessage.FromEnvelope<TraceRouteMessage>(envelope, decoded, recipient);
                msg.RouteDiscovery = routeDiscovery;
                return (true, msg);
            }
            else if (decoded.Portnum == PortNum.PositionApp)
            {
                if (envelope.Packet.To != _options.MeshtasticNodeId
                    && envelope.Packet.To != BroadcastDeviceId)
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient));
                }

                var position = Position.Parser.ParseFrom(decoded.Payload);

                if (position == null
                    || !position.HasLatitudeI
                    || !position.HasLongitudeI)
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient));
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

                var msg = MeshMessage.FromEnvelope<PositionMessage>(envelope, decoded, recipient);
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
                var msg = MeshMessage.FromEnvelope<TextMessage>(envelope, decoded, recipient);
                msg.Text = decoded.Payload.ToStringUtf8();
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
                var telemetry = Telemetry.Parser.ParseFrom(decoded.Payload);

                if (telemetry == null
                    || telemetry.DeviceMetrics == null
                    || (!telemetry.DeviceMetrics.HasChannelUtilization
                    && !telemetry.DeviceMetrics.HasAirUtilTx))
                {
                    return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient));
                }

                var msg = MeshMessage.FromEnvelope<DeviceMetricsMessage>(envelope, decoded, recipient);
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
                return (true, MeshMessage.FromEnvelope<UnknownMeshMessage>(envelope, decoded, recipient));
            }
        }

        public static (bool success, Data data) DecryptPKI(byte[] recipientPrivateKey, byte[] senderPublicKey, MeshPacket meshPacket)
        {
            if (meshPacket.Encrypted == null || meshPacket.Encrypted.Length == 0) return (false, null);
            var encryptedData = meshPacket.Encrypted.ToByteArray();
            var decrypted = PKIEncryption.Decrypt(recipientPrivateKey, senderPublicKey, encryptedData, meshPacket.Id, meshPacket.From);
            var data = Data.Parser.ParseFrom(decrypted);
            return (true, data);
        }


        public static double PrecisionBitsToAccuracyMeters(int precisionBits)
        {
            const double EarthCircumference = 40075016.0; // meters
            if (precisionBits < 1 || precisionBits > 32)
                throw new ArgumentOutOfRangeException(nameof(precisionBits), "precisionBits must be between 1 and 32.");

            int possibleValues = 1 << precisionBits;
            double degreeStep = 180.0 / possibleValues;
            double metersPerDegree = EarthCircumference / 360.0;
            double accuracyMeters = (degreeStep * metersPerDegree) / 2.0;
            return accuracyMeters;
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
            return _localMessageQueueService.EstimateDelay(networkId, priority);
        }

        public int GetQueueLength(int networkId) => _localMessageQueueService.GetQueueLength(networkId);

        public TimeSpan SingleMessageQueueDelay => _localMessageQueueService.SingleMessageQueueDelay;

        private static AckMessage DecodeAck(ServiceEnvelope envelope, Data decoded, IRecipient recipient)
        {
            var routing = Routing.Parser.ParseFrom(decoded.Payload);
            var msg = MeshMessage.FromEnvelope<AckMessage>(envelope, decoded, recipient);
            msg.NeedAck = false;
            msg.AckedMessageId = decoded.RequestId;
            msg.IsPkiEncrypted = envelope.Packet.PkiEncrypted;
            msg.Success = routing.ErrorReason == Routing.Types.Error.None;
            return msg;
        }

        private (bool success, MeshMessage msg) DecodeNodeInfo(ServiceEnvelope envelope, Data decoded, IRecipient recipient)
        {
            var user = User.Parser.ParseFrom(decoded.Payload);
            if (user?.PublicKey == null
                || user.PublicKey.Length != PkiKeyLength)
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
            var msg = MeshMessage.FromEnvelope<NodeInfoMessage>(envelope, decoded, recipient);
            msg.DeviceId = deviceId;
            msg.NodeName = user.LongName ?? user.ShortName ?? user.Id;
            msg.PublicKey = user.PublicKey.ToByteArray();
            return (true, msg);
        }

        public void IncreaseBridgeDirectMessagesToGatewaysStat(int networkId)
        {
            AddStat(new MeshStat
            {
                NetworkId = networkId,
                BridgeDirectMessagesToGateways = 1
            });
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
