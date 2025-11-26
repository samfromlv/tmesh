using Google.Protobuf;
using Meshtastic.Protobufs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Shared.Models;
using System.Text;
using TBot.Helpers;
using TBot.Models;
using TBot.Models.MeshMessages;

namespace TBot
{
    public class MeshtasticService
    {
        public const int MaxTextMessageBytes = 233 - MESHTASTIC_PKC_OVERHEAD;
        public const int WaitForAckStatusMaxMinutes = 2;
        const int MESHTASTIC_PKC_OVERHEAD = 12;
        private const int NoDupExpirationMinutes = 3;
        private const int PkiKeyLength = 32;
        private const int ReplyHopsMargin = 2;
        private const int KeepStatsForMinutes = 60;
        private const int TraceRouteSNRDefault = sbyte.MinValue;
        private const uint BroadcastDeviceId = uint.MaxValue;
        private static readonly LinkedList<MeshStat> meshStats = new();
        private readonly LinkedList<MeshStat> _meshStatsQueue = meshStats;
        private byte[] _macAddress;


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
            _localMessageQueueService.SendMessage += LocalMessageQueueService_SendMessage;
            _macAddress = GetMacAddressFromNodeId();
        }

        private Task LocalMessageQueueService_SendMessage(DataEventArgs<QueuedMessage> arg)
        {
            return _mqttService.PublishMeshtasticMessage(
                arg.Data.Message,
                arg.Data.RelayThroughGatewayId);
        }

        private readonly MqttService _mqttService;
        private readonly ILogger<MeshtasticService> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly TBotOptions _options;
        private readonly LocalMessageQueueService _localMessageQueueService;



        public QueueResult SendPublicTextMessage(
            string text,
            long? relayGatewayId)
        {
            var envelope = PackPublicTextMessage(
                GenerateNewMessageId(),
                text,
                null);
            AddStat(new MeshStat
            {
                TextMessagesSent = 1,
            });
            var delay = QueueMessage(envelope, MessagePriority.Normal, relayGatewayId);
            return new QueueResult
            {
                MessageId = envelope.Packet.Id,
                EstimatedSendDelay = delay
            };
        }
        public QueueResult SendTextMessage(
            long deviceId,
            byte[] publicKey,
            string text,
            long? replyToMessageId,
            long? relayGatewayId)
        {
            return SendTextMessage(GenerateNewMessageId(),
                deviceId,
                publicKey,
                text,
                replyToMessageId,
                relayGatewayId);
        }

        public QueueResult SendTextMessage(
            long newMessageId,
            long deviceId,
            byte[] publicKey,
            string text,
            long? replyToMessageId,
            long? relayGatewayId)
        {
            _logger.LogInformation("Sending message to device {DeviceId}: {Message}", deviceId, text);
            var envelope = PackTextMessage(
                newMessageId,
                deviceId,
                publicKey,
                text,
                replyToMessageId);
            AddStat(new MeshStat
            {
                TextMessagesSent = 1,
            });
            var delay = QueueMessage(envelope, MessagePriority.Normal, relayGatewayId);
            return new QueueResult
            {
                MessageId = envelope.Packet.Id,
                EstimatedSendDelay = delay
            };
        }

        public void AckMeshtasticMessage(
            byte[] publicKey,
            MeshMessage msg,
            long? relayGatewayId)
        {
            var hopsUsed = msg.HopStart - msg.HopLimit;
            var hopsForReply = Math.Max(1, hopsUsed + ReplyHopsMargin);
            var envelope = PackAckMessage(msg.DeviceId, publicKey, msg.Id, hopsForReply);
            AddStat(new MeshStat
            {
                AckSent = 1
            });
            QueueMessage(envelope, MessagePriority.High, relayGatewayId);
        }

        public static bool IsBroadcastDeviceId(long deviceId)
        {
            return deviceId == BroadcastDeviceId;
        }

        public void SendTraceRouteResponse(TraceRouteMessage msg,
            long? relayGatewayId)
        {
            var hopsUsed = msg.HopStart - msg.HopLimit;
            var hopsForReply = Math.Max(1, hopsUsed + ReplyHopsMargin);
            var envelope = PackTraceRouteResponse(
                msg.DeviceId,
                msg.RouteDiscovery,
                msg.Id,
                hopsUsed,
                hopsForReply);

            AddStat(new MeshStat
            {
                TraceRoutes = 1
            });
            QueueMessage(envelope, MessagePriority.Low, relayGatewayId);
        }

        public void NakNoPubKeyMeshtasticMessage(MeshMessage msg,
            long? relayGatewayId)
        {
            var hopsUsed = msg.HopStart - msg.HopLimit;
            var hopsForReply = Math.Max(1, hopsUsed + ReplyHopsMargin);
            var envelope = PackNoPublicKeyMessage(msg.DeviceId, msg.Id, hopsForReply);
            AddStat(new MeshStat
            {
                NakSent = 1
            });
            QueueMessage(envelope, MessagePriority.Low, relayGatewayId);
        }

        public void AckMeshtasticMessage(
            long deviceId,
            byte[] publicKey,
            MeshPacket packet,
            long? relayGatewayId)
        {
            var hopsUsed = packet.HopStart - packet.HopLimit;
            var hopsForReply = (int)Math.Max(1, hopsUsed + ReplyHopsMargin);
            var envelope = PackAckMessage(deviceId, publicKey, packet.Id, hopsForReply);
            QueueMessage(envelope, MessagePriority.High, relayGatewayId);
        }



        private TimeSpan QueueMessage(
            ServiceEnvelope envelope,
            MessagePriority messagePriority,
            long? relayThroughGatewayId)
        {
            var id = envelope.Packet.Id;
            StoreNoDup(id);
            return _localMessageQueueService.EnqueueMessage(new QueuedMessage
            {
                Message = envelope,
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
            long? replyToMessageId)
        {
            var packet = CreateTextMessagePacket(
                newMessageId,
                deviceId,
                publicKey,
                text,
                replyToMessageId);
            var envelope = CreateMeshtasticEnvelope(packet, "PKI");
            return envelope;
        }

        private ServiceEnvelope PackPublicTextMessage(
            long newMessageId,
            string text,
            long? replyToMessageId)
        {
            var packet = CreateTextMessagePacket(
                newMessageId,
                deviceId: BroadcastDeviceId,
                null,
                text,
                replyToMessageId);
            packet = EncryptPacketWithPsk(packet);
            var envelope = CreateMeshtasticEnvelope(packet, _options.MeshtasticPrimaryChannelName);
            return envelope;
        }

        private ServiceEnvelope PackAckMessage(long deviceId, byte[] publicKey, long messageId, int messageHopLimit)
        {
            var packet = CreateAckMessagePacket(deviceId, publicKey, messageId, messageHopLimit);
            var envelope = CreateMeshtasticEnvelope(packet, "PKI");
            return envelope;
        }

        private ServiceEnvelope PackTraceRouteResponse(
            long deviceId,
            RouteDiscovery routeDiscovery,
            long messageId,
            int hopsUsed,
            int messageHopLimit)
        {
            var packet = CreateTraceRouteResponsePacket(
                deviceId,
                routeDiscovery,
                messageId,
                hopsUsed,
                messageHopLimit);
            var envelope = CreateMeshtasticEnvelope(packet, "PKI");
            return envelope;
        }

        private ServiceEnvelope PackNoPublicKeyMessage(long deviceId, long messageId, int messageHopLimit)
        {
            var packet = CreateNoPublicKeyMessagePacket(deviceId, messageId, messageHopLimit);
            var envelope = CreateMeshtasticEnvelope(packet, "PKI");
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
            return "!" + deviceId.ToString("X").ToLower();
        }

        private MeshPacket CreateTextMessagePacket(
            long newMessageId,
            long deviceId,
            byte[] publicKey,
            string text,
            long? replyToMessageId)
        {
            var bytes = ByteString.CopyFromUtf8(text);
            if (bytes.Length > MaxTextMessageBytes)
                throw new ArgumentException($"Message too long for Meshtastic ({bytes.Length} bytes, max {MaxTextMessageBytes})");

            var packet = new MeshPacket()
            {
                Channel = 0,
                WantAck = true,
                To = (uint)deviceId,
                From = (uint)_options.MeshtasticNodeId,
                Priority = MeshPacket.Types.Priority.Default,
                Id = (uint)newMessageId,
                HopLimit = (uint)_options.OutgoingMessageHopLimit,
                HopStart = (uint)_options.OutgoingMessageHopLimit,
                Decoded = new Data()
                {
                    ReplyId = replyToMessageId.HasValue ? (uint)replyToMessageId.Value : 0,
                    Portnum = PortNum.TextMessageApp,
                    Payload = bytes,
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
                    Bitfield = 1,
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
           int messageHopLimit)
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
                    Bitfield = 1,
                    Payload = routeDiscovery.ToByteString(),
                },
            };
            packet = EncryptPacketWithPsk(packet);
            return packet;
        }

        public MeshPacket CreateNoPublicKeyMessagePacket(
          long deviceId,
          long messageId,
          int messageHopLimit)
        {
            var routeData = new Routing
            {
                ErrorReason = Routing.Types.Error.PkiUnknownPubkey
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
                    Bitfield = 1,
                    Payload = routeData.ToByteString(),
                },
            };

            packet = EncryptPacketWithPsk(packet);

            return packet;
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

        public void SendVirtualNodeInfo()
        {
            var packet = CreateTMeshVirtualNodeInfo();
            var envelope = CreateMeshtasticEnvelope(packet, _options.MeshtasticPrimaryChannelName);
            var id = envelope.Packet.Id;
            StoreNoDup(id);
            _localMessageQueueService.EnqueueMessage(new QueuedMessage
            {
                Message = envelope
            }, MessagePriority.Low);
        }

        private void StoreNoDup(uint id)
        {
            _memoryCache.Set(GetNoDupMessageKey(id), true, TimeSpan.FromMinutes(NoDupExpirationMinutes));
        }

        private bool TryStoreNoDup(uint id)
        {
            var key = GetNoDupMessageKey(id);
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

        private MeshPacket CreateTMeshVirtualNodeInfo()
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
                Decoded = new Meshtastic.Protobufs.Data()
                {
                    Portnum = PortNum.NodeinfoApp,
                    Payload = new User
                    {
                        HwModel = HardwareModel.DiyV1,
                        Id = GetMeshtasticNodeHexId(_options.MeshtasticNodeId),
                        IsLicensed = false,
                        Macaddr = ByteString.CopyFrom(_macAddress),
                        LongName = _options.MeshtasticNodeNameLong,
                        IsUnmessagable = false, // Field name from external library kept as-is.
                        ShortName = _options.MeshtasticNodeNameShort,
                        Role = Config.Types.DeviceConfig.Types.Role.ClientHidden,
                        PublicKey = ByteString.FromBase64(_options.MeshtasticPublicKeyBase64)
                    }.ToByteString(),
                },
            };

            packet = EncryptPacketWithPsk(packet);
            return packet;
        }

        private MeshPacket EncryptPacketWithPsk(MeshPacket packet)
        {
            var input = packet.Decoded.ToByteArray();
            var nonce = new Meshtastic.Crypto.NonceGenerator(packet.From, packet.Id);
            var key = Convert.FromBase64String(_options.MeshtasticPrimaryChannelPskBase64);
            if (key.Length == 1 && key[0] == 1)
            {
                key = Meshtastic.Resources.DEFAULT_PSK;
            }
            var encrypted = TransformPacket(input, nonce.Create(), key, true);
            packet.Encrypted = ByteString.CopyFrom(encrypted);
            packet.Channel = GenerateChannelHash(_options.MeshtasticPrimaryChannelName, key);

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

        public static uint GenerateChannelHash(string name, byte[] key)
        {
            if (string.IsNullOrEmpty(name) || key == null || key.Length == 0)
                return 0;

            byte h = XorHash(Encoding.UTF8.GetBytes(name));
            h ^= XorHash(key);

            return h;
        }

        public MeshPacket DecryptPacketWithPsk(MeshPacket packet)
        {
            var input = packet.Encrypted.ToByteArray();
            var nonce = new Meshtastic.Crypto.NonceGenerator(packet.From, packet.Id);
            var key = Convert.FromBase64String(_options.MeshtasticPrimaryChannelPskBase64);
            if (key.Length == 1 && key[0] == 1)
            {
                key = Meshtastic.Resources.DEFAULT_PSK;
            }
            var decrypted = TransformPacket(input, nonce.Create(), key, false);
            packet.Decoded = Data.Parser.ParseFrom(decrypted);
            if (packet.Decoded.Portnum == PortNum.UnknownApp)
            {
                return null;
            }

            return packet;
        }

        public static byte[] TransformPacket(byte[] input, byte[] nonce, byte[] key, bool forEncryption)
        {
            // Create a new cipher instance for each call
            IBufferedCipher cipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");

            // Initialize for encryption or decryption
            cipher.Init(forEncryption, new ParametersWithIV(ParameterUtilities.CreateKeyParameter("AES", key), nonce));

            // Perform the transformation
            return cipher.DoFinal(input);
        }

        public static (bool isPki, long senderDeviceId, long receiverDeviceId) GetMessageSenderDeviceId(
            ServiceEnvelope envelope)
        {
            if (envelope?.Packet == null)
            {
                return default;
            }
            if (envelope.Packet.PkiEncrypted)
            {
                return (true, envelope.Packet.From, envelope.Packet.To);
            }
            else
            {
                return (false, envelope.Packet.From, envelope.Packet.To);
            }
        }

        public (bool success, MeshMessage msg) TryDecryptMessage(
            ServiceEnvelope envelope,
            byte[] SenderPublicKey)
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
            if (!TryStoreNoDup(id))
            {
                AddStat(new MeshStat
                {
                    DupsIgnored = 1,
                });
                _logger.LogDebug("Duplicate Meshtastic message received, ignoring. Id: {Id}", id);
                return default;
            }

            if (!envelope.Packet.PkiEncrypted)
            {
                var packet = DecryptPacketWithPsk(envelope.Packet);
                if (packet?.Decoded == null)
                {
                    return default;
                }

                if (packet.Decoded.Portnum == PortNum.NodeinfoApp)
                {
                    return DecodeNodeInfo(envelope, packet.Decoded);
                }
                else if (packet.Decoded.Portnum == PortNum.RoutingApp
                    && packet.To == _options.MeshtasticNodeId)
                {
                    AddStat(new MeshStat
                    {
                        AckRecieved = 1,
                    });
                    return DecodeAck(envelope, packet);
                }
                else if (packet.Decoded.Portnum == PortNum.TracerouteApp
                    && packet.To == _options.MeshtasticNodeId)
                {
                    var routeDiscovery = RouteDiscovery.Parser.ParseFrom(packet.Decoded.Payload);

                    if (!routeDiscovery.Route.Contains((uint)gatewayNodeId)
                        && packet.From != gatewayNodeId)
                    {
                        routeDiscovery.Route.Add((uint)gatewayNodeId);
                        routeDiscovery.SnrTowards.Add(RoundSnrForTrace(packet.RxSnr));
                    }

                    return (true, new TraceRouteMessage
                    {
                        DeviceId = envelope.Packet.From,
                        GatewayId = PraseDeviceHexId(envelope.GatewayId),
                        NeedAck = envelope.Packet.WantAck
                             && envelope.Packet.From != BroadcastDeviceId
                             && envelope.Packet.To != BroadcastDeviceId,
                        HopLimit = (int)envelope.Packet.HopLimit,
                        HopStart = (int)envelope.Packet.HopStart,
                        Id = envelope.Packet.Id,
                        RouteDiscovery = routeDiscovery,
                    });
                }
                else if (packet.Decoded.Portnum == PortNum.PositionApp
                    && packet.To == _options.MeshtasticNodeId)
                {
                    var position = Position.Parser.ParseFrom(packet.Decoded.Payload);

                    if (position == null
                        || !position.HasLatitudeI
                        || !position.HasLongitudeI)
                    {
                        return default;
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

                    return (true, new PositionMessage
                    {
                        DeviceId = envelope.Packet.From,
                        GatewayId = PraseDeviceHexId(envelope.GatewayId),
                        NeedAck = envelope.Packet.WantAck
                             && envelope.Packet.From != BroadcastDeviceId
                             && envelope.Packet.To != BroadcastDeviceId,
                        HopLimit = (int)envelope.Packet.HopLimit,
                        HopStart = (int)envelope.Packet.HopStart,
                        Id = envelope.Packet.Id,
                        Latitude = position.LatitudeI / 1e7,
                        Longitude = position.LongitudeI / 1e7,
                        HeadingDegrees = position.HasGroundTrack
                            ? MeshtasticPositionUtils.GroundTrackToHeading((int)position.GroundTrack)
                            : null,
                        Altitude = position.HasAltitude
                            ? position.Altitude
                            : null,
                        AccuracyMeters = accuracyMeters
                    });
                }
                else
                {
                    return default;
                }
            }

            if (envelope.Packet.To != _options.MeshtasticNodeId)
                return default;

            if (SenderPublicKey == null)
            {
                return (true, new EncryptedDirectMessage
                {
                    GatewayId = PraseDeviceHexId(envelope.GatewayId),
                    NeedAck = envelope.Packet.WantAck
                        && envelope.Packet.From != BroadcastDeviceId
                        && envelope.Packet.To != BroadcastDeviceId,
                    DeviceId = envelope.Packet.From,
                    HopLimit = (int)envelope.Packet.HopLimit,
                    HopStart = (int)envelope.Packet.HopStart,
                    Id = envelope.Packet.Id,
                });
            }

            if (!Meshtastic.Crypto.PKIEncryption.Decrypt(
                 Convert.FromBase64String(_options.MeshtasticPrivateKeyBase64),
                 SenderPublicKey,
                 envelope.Packet
             ))
            {
                return default;
            }

            if (envelope.Packet.Decoded.Portnum == PortNum.TextMessageApp)
            {
                var res = new TextMessage
                {
                    GatewayId = PraseDeviceHexId(envelope.GatewayId),
                    NeedAck = envelope.Packet.WantAck
                        && envelope.Packet.From != BroadcastDeviceId
                        && envelope.Packet.To != BroadcastDeviceId,
                    Id = envelope.Packet.Id,
                    HopLimit = (int)envelope.Packet.HopLimit,
                    HopStart = (int)envelope.Packet.HopStart,
                    Text = envelope.Packet.Decoded.Payload.ToStringUtf8(),
                    DeviceId = envelope.Packet.From,
                    ReplyTo = envelope.Packet.Decoded.ReplyId
                };
                AddStat(new MeshStat
                {
                    TextMessagesRecieved = 1,
                });

                return (true, res);
            }
            else if (envelope.Packet.Decoded.Portnum == PortNum.RoutingApp)
            {
                AddStat(new MeshStat
                {
                    AckRecieved = 1,
                });
                return DecodeAck(envelope, envelope.Packet);
            }
            else if (envelope.Packet.Decoded.Portnum == PortNum.NodeinfoApp)
            {
                return DecodeNodeInfo(envelope, envelope.Packet.Decoded);
            }
            return default;
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

        public TimeSpan EstimateDelay(MessagePriority priority)
        {
            return _localMessageQueueService.EstimateDelay(priority);
        }

        public TimeSpan SingleMessageQueueDelay => _localMessageQueueService.SingleMessageQueueDelay;

        private static (bool success, MeshMessage msg) DecodeAck(ServiceEnvelope envelope, MeshPacket packet)
        {
            var routing = Routing.Parser.ParseFrom(packet.Decoded.Payload);
            return (true, new AckMessage
            {
                DeviceId = envelope.Packet.From,
                NeedAck = false,
                HopLimit = (int)envelope.Packet.HopLimit,
                GatewayId = PraseDeviceHexId(envelope.GatewayId),
                HopStart = (int)envelope.Packet.HopStart,
                Id = envelope.Packet.Id,
                AckedMessageId = packet.Decoded.RequestId,
                IsPkiEncrypted = envelope.Packet.PkiEncrypted,
                Success = routing.ErrorReason == Routing.Types.Error.None
            });
        }

        private (bool success, MeshMessage msg) DecodeNodeInfo(ServiceEnvelope envelope, Data decoded)
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
                NodeInfoRecieved = 1,
            });
            return (true, new NodeInfoMessage
            {
                DeviceId = deviceId,
                NeedAck = envelope.Packet.WantAck
                    && envelope.Packet.To != BroadcastDeviceId
                    && deviceId != BroadcastDeviceId,
                GatewayId = PraseDeviceHexId(envelope.GatewayId),
                HopLimit = (int)envelope.Packet.HopLimit,
                HopStart = (int)envelope.Packet.HopStart,
                Id = envelope.Packet.Id,
                NodeName = user.LongName ?? user.ShortName ?? user.Id,
                PublicKey = user.PublicKey.ToByteArray(),
            });
        }

        public void IncreaseBridgeDirectMessagesToGatewaysStat()
        {
            AddStat(new MeshStat
            {
                BridgeDirectMessagesToGateways = 1
            });
        }

        public MeshStat AggregateStartFrom(DateTime fromUtc)
        {
            var aggregate = new MeshStat()
            {
                IntervalStart = fromUtc
            };
            lock (_meshStatsQueue)
            {
                foreach (var stat in _meshStatsQueue)
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
            lock (_meshStatsQueue)
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

                var existingStat = _meshStatsQueue.First?.Value;

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
                _meshStatsQueue.AddFirst(stat);
                var border = roundedUtcNow.AddMinutes(-KeepStatsForMinutes);
                while (_meshStatsQueue.Count > 0 && _meshStatsQueue.Last.Value.IntervalStart < border)
                {
                    _meshStatsQueue.RemoveLast();
                }
            }
        }
    }
}
