using Google.Protobuf;
using Meshtastic.Protobufs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using System.Text;
using TBot.Helpers;
using TBot.Models;

namespace TBot
{
    public class MeshtasticService
    {
        public const int MaxTextMessageBytes = 233 - MESHTASTIC_PKC_OVERHEAD;
        const int MESHTASTIC_PKC_OVERHEAD = 12;
        const int TrimUserNamesToLength = 8;
        private const int NoDupExpirationMinutes = 3;
        private const int PkiKeyLength = 32;

        public MeshtasticService(
            MqttService mqttService,
            IMemoryCache memoryCache,
            IOptions<TBotOptions> options,
            ILogger<MeshtasticService> logger)
        {
            _mqttService = mqttService;
            _logger = logger;
            _memoryCache = memoryCache;
            _options = options.Value;
        }

        private readonly MqttService _mqttService;
        private readonly ILogger<MeshtasticService> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly TBotOptions _options;


        public async Task SendMeshtasticMessage(long deviceId, byte[] publicKey, string userName, string text)
        {
            var message = $"{StringHelper.Truncate(userName, TrimUserNamesToLength)}: {text}";
            _logger.LogInformation("Sending message to device {DeviceId}: {Message}", deviceId, message);
            await QueueTextMessage(deviceId, publicKey, message);
        }

        public async Task SendMeshtasticMessage(long deviceId, byte[] publicKey, string text)
        {
            _logger.LogInformation("Sending message to device {DeviceId}: {Message}", deviceId, text);
            await QueueTextMessage(deviceId, publicKey, text);
        }

        private async Task QueueTextMessage(long deviceId, byte[] publicKey, string text)
        {
            var envelope = PackTextMessage(deviceId, publicKey, text);
            var id = envelope.Packet.Id;
            StoreNoDup(id);
            await _mqttService.PublishMeshtasticMessage(envelope);
        }

        private static string GetNoDupMessageKey(uint id)
        {
            return $"meshtastic:outgoing:{id:X}";
        }

        private ServiceEnvelope PackTextMessage(long deviceId, byte[] publicKey, string text)
        {
            var packet = CreateTextMessagePacket(deviceId, publicKey, text);
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

        private MeshPacket CreateTextMessagePacket(long deviceId, byte[] publicKey, string text)
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
                Id = (uint)Math.Floor(Random.Shared.Next() * 1e9),
                HopLimit = (uint)_options.OutgoingMessageHopLimit,
                HopStart = (uint)_options.OutgoingMessageHopLimit,
                Decoded = new Meshtastic.Protobufs.Data()
                {
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


        public bool TryParseDeviceId(string input, out long deviceId)
        {
            input = input.Trim();
            if (input.StartsWith("!", StringComparison.OrdinalIgnoreCase)
                || input.StartsWith("#", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(input.Substring(1),
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out deviceId))
                    return true;
            }
            if (long.TryParse(input, out deviceId)) return true;
            deviceId = 0;
            return false;
        }

        public (string publicKeyBase64, string privateKeyBase64) GenerateKeyPair()
        {
            var keyPair = Meshtastic.Crypto.PKIEncryption.GenerateKeyPair();
            return (Convert.ToBase64String(keyPair.publicKey), Convert.ToBase64String(keyPair.privateKey));
        }

        public bool CanSendMessage(string userName, string text)
        {
            var byteCount = Encoding.UTF8.GetByteCount(text);
            byteCount += Encoding.UTF8.GetByteCount(
                StringHelper.Truncate(userName, TrimUserNamesToLength));
            byteCount += 2; // for ": "
            return byteCount <= MaxTextMessageBytes;
        }

        public async Task SendVirtualNodeInfoAsync()
        {
            var packet = CreateTMeshVirtualNodeInfo();
            var envelope = CreateMeshtasticEnvelope(packet, _options.MeshtasticPrimaryChannelName);
            var id = envelope.Packet.Id;
            StoreNoDup(id);
            await _mqttService.PublishMeshtasticMessage(envelope);
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

        private MeshPacket CreateTMeshVirtualNodeInfo()
        {
            var packet = new MeshPacket()
            {
                Channel = 0,
                WantAck = false,
                To = uint.MaxValue,
                From = (uint)_options.MeshtasticNodeId,
                Priority = MeshPacket.Types.Priority.Background,
                Id = (uint)Math.Floor(Random.Shared.Next() * 1e9),
                HopLimit = (uint)_options.OwnNodeInfoMessageHopLimit,
                HopStart = (uint)_options.OwnNodeInfoMessageHopLimit,
                Decoded = new Meshtastic.Protobufs.Data()
                {
                    Portnum = PortNum.NodeinfoApp,
                    Payload = new User
                    {
                        HwModel = HardwareModel.Unset,
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

        public (bool isPki, long senderDeviceId, long receiverDeviceId) GetMessageSenderDeviceId(
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

            if (envelope.GatewayId == GetMeshtasticNodeHexId(_options.MeshtasticNodeId))
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
                    var nodeInfo = NodeInfo.Parser.ParseFrom(packet.Decoded.ToByteArray());
                    if (nodeInfo?.User?.PublicKey == null
                        || nodeInfo.User.PublicKey.Length != PkiKeyLength)
                    {
                        return default;
                    }
                    long deviceId = packet.From;
                    if (TryParseDeviceId(nodeInfo.User.Id, out var parsedId))
                    {
                        deviceId = parsedId;
                    }

                    return (true, new NodeInfoMessage
                    {
                        DeviceId = deviceId,
                        NodeName = nodeInfo.User.LongName ?? nodeInfo.User.ShortName ?? nodeInfo.User.Id,
                        PublicKey = nodeInfo.User.PublicKey.ToByteArray(),
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
                return default;
            }

            if (!Meshtastic.Crypto.PKIEncryption.Decrypt(
                 Convert.FromBase64String(_options.MeshtasticPrivateKeyBase64),
                 SenderPublicKey,
                 envelope.Packet
             ))
            {
                return default;
            }

            var decoded = envelope.Packet.Decoded;
            if (decoded.Portnum != PortNum.TextMessageApp)
                return default;

            var res = new TextMessage
            {
                Text = decoded.Payload.ToStringUtf8(),
                DeviceId = envelope.Packet.From,
            };

            return (true, res);
        }

    }
}
