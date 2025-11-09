using Meshtastic.Protobufs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MQTTnet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Models;

namespace TBot
{
    public class MeshtasticService
    {
        const int MaxMessageBytes = 230;
        const int TrimUserNamesToLength = 8;


        public MeshtasticService(
            MqttService mqttService,
            IMemoryCache memoryCache,
            ILogger<MeshtasticService> logger)
        {
            _mqttService = mqttService;
            _logger = logger;
            _memoryCache = memoryCache;
            _mqttService.MeshtasticMessageReceivedAsync += HandleIncomingMessage;
        }

        private readonly MqttService _mqttService;
        private readonly ILogger<MeshtasticService> _logger;
        private readonly IMemoryCache _memoryCache;


        public async Task SendMeshtasticMessage(long deviceId, string userName, string text)
        {
            await _mqttService.EnsureMqttConnectedAsync();




            var message = $"{userName.Substring(0, Math.Min(userName.Length, TrimUserNamesToLength))}: {text}";

            _logger.LogInformation("Sending message to device {DeviceId}: {Message}", deviceId, message);

            // Implementation for sending a message to the Meshtastic device
        }

        public async Task SendMeshtasticMessage(long deviceId, string text)
        {
            _logger.LogInformation("Sending message to device {DeviceId}: {Message}", deviceId, text);

            // Implementation for sending a message to the Meshtastic device
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



        public bool CanSendMessage(string userName, string text)
        {
            var byteCount = Encoding.UTF8.GetByteCount(text);
            byteCount += Encoding.UTF8.GetByteCount(
                userName.Substring(0, Math.Min(userName.Length, TrimUserNamesToLength)));
            byteCount += 2; // for ": "
            return byteCount <= MaxMessageBytes;
        }

        private async Task HandleIncomingMessage(DataEventArgs<ServiceEnvelope> msg)
        {
            var envelope = msg.Data;
            if (envelope.Packet.PayloadVariantCase != MeshPacket.PayloadVariantOneofCase.Decoded)
                return;

            var id = envelope.Packet.Id;

            if (_memoryCache.TryGetValue($"meshtastic:incoming:{id}", out _))
            {
                _logger.LogDebug("Duplicate Meshtastic message received, ignoring. Id: {Id}", id);
                return;
            }

            _memoryCache.Set($"meshtastic:incoming:{id}", true, TimeSpan.FromMinutes(3));
        }

    }
}
