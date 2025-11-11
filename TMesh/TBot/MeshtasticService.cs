using Google.Protobuf;
using Meshtastic.Protobufs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Models;

namespace TBot
{
    public class MeshtasticService
    {
        const int MaxMessageBytes = 233;
        const int TrimUserNamesToLength = 8;
        private const int NoDupExpirationMinutes = 3;

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
            var message = $"{userName.Substring(0, Math.Min(userName.Length, TrimUserNamesToLength))}: {text}";
            _logger.LogInformation("Sending message to device {DeviceId}: {Message}", deviceId, message);
            await QueueTextMessage(deviceId, publicKey, text);
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
            var envelope = CreateMeshtasticEnvelope(packet);
            return envelope;
        }

        private ServiceEnvelope CreateMeshtasticEnvelope(MeshPacket packet)
        {
            return new ServiceEnvelope()
            {
                Packet = packet,
                ChannelId = "PKI",
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
            if (bytes.Length > MaxMessageBytes)
                throw new ArgumentException($"Message too long for Meshtastic ({bytes} bytes, max {MaxMessageBytes})");



            var packet = new MeshPacket()
            {
                Channel = 0,//todo: channel,
                WantAck = false,
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
                userName.Substring(0, Math.Min(userName.Length, TrimUserNamesToLength)));
            byteCount += 2; // for ": "
            return byteCount <= MaxMessageBytes;
        }


        public async Task SendVirtualNodeInfoAsync()
        {
            var packet = CreateTMeshVirtaulNodeInfo();
            var envelope = CreateMeshtasticEnvelope(packet);
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

        private MeshPacket CreateTMeshVirtaulNodeInfo()
        {

            return new MeshPacket()
            {
                Channel = 1,
                WantAck = false,
                To = uint.MaxValue,
                From = (uint)_options.MeshtasticNodeId,
                Priority = MeshPacket.Types.Priority.Background,
                Id = (uint)Math.Floor(Random.Shared.Next() * 1e9),
                HopLimit = 1,//(uint)_options.OutgoingMessageHopLimit,
                HopStart = 1,//(uint)_options.OutgoingMessageHopLimit,
                Decoded = new Meshtastic.Protobufs.Data()
                {
                    Portnum = PortNum.NodeinfoApp,
                    Payload = new User
                    {
                        HwModel = HardwareModel.Unset,
                        Id = GetMeshtasticNodeHexId(_options.MeshtasticNodeId),
                        IsLicensed = false,
                        LongName = _options.MeshtasticNodeNameLong,
                        IsUnmessagable = false,
                        ShortName = _options.MeshtasticNodeNameShort,
                        Role = Config.Types.DeviceConfig.Types.Role.ClientHidden,
                        PublicKey = ByteString.FromBase64(_options.MeshtasticPublicKeyBase64)
                    }.ToByteString(),
                },
            };
        }

        public (bool success, TextMessage msg) ShouldHandleMessage(ServiceEnvelope envelope)
        {
            if (envelope.Packet.PayloadVariantCase != MeshPacket.PayloadVariantOneofCase.Decoded)
                return default;

            var id = envelope.Packet.Id;

            if (!TryStoreNoDup(id))
            {
                _logger.LogDebug("Duplicate Meshtastic message received, ignoring. Id: {Id}", id);
                return default;
            }


            var decoded = envelope.Packet.Decoded;
            if (decoded.Portnum != PortNum.TextMessageApp)
                return default;

            var res = new TextMessage
            {
                Text = decoded.Payload.ToStringUtf8(),
                DeviceId = envelope.Packet.From,
                PublicKey = envelope.Packet.PkiEncrypted ? envelope.Packet.PublicKey.ToByteArray() : null,
            };

            return (true, res);
        }

    }
}
