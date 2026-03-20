using Meshtastic.Discovery;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Models;
using TBot.Models.MeshMessages;
using Telegram.Bot.Types;

namespace TBot.Bot
{
    public class BotCache
        (IMemoryCache memoryCache,
        MeshtasticService meshtasticService,
        IOptions<TBotOptions> options)

    {
        private readonly TBotOptions _options = options.Value;

        public DeviceAndGatewayId GetSingleDeviceChannelGateway(int channelId)
        {
            if (_options.DirectGatewayRoutingSeconds <= 0)
            {
                return null;
            }
            var cacheKey = $"ChannelGateway_{channelId}";
            if (memoryCache.TryGetValue(cacheKey, out DeviceAndGatewayId deviceAndGatewayId))
            {
                return deviceAndGatewayId;
            }
            return null;
        }

        public DeviceAndGatewayId GetDeviceGateway(long deviceId)
        {
            if (_options.DirectGatewayRoutingSeconds <= 0)
            {
                return null;
            }
            var cacheKey = $"DeviceGateway_{deviceId}";
            if (memoryCache.TryGetValue(cacheKey, out DeviceAndGatewayId gatewayId))
            {
                return gatewayId;
            }
            return null;
        }

        public void StoreTelegramMessageStatus(
         long chatId,
         int messageId,
         MeshtasticMessageStatus status)
        {
            var currentMeshQueueDelay = meshtasticService.EstimateDelay(MessagePriority.Normal);

            var cacheKey = $"TelegramMessageStatus_{chatId}_{messageId}";
            memoryCache.Set(cacheKey, status, currentMeshQueueDelay.Add(TimeSpan.FromMinutes(Math.Max(currentMeshQueueDelay.TotalMinutes * 1.3, 3))));
        }

        public void StoreGatewayRegistraionChat(long deviceId, long chatId)
        {
            memoryCache.Set($"GatewayRegistrationChat_{deviceId}", chatId, TimeSpan.FromMinutes(60));
        }

        public long? GetGatewayRegistraionChat(long deviceId)
        {
            var cacheKey = $"GatewayRegistrationChat_{deviceId}";
            if (memoryCache.TryGetValue(cacheKey, out long chatId))
            {
                return chatId;
            }
            return null;
        }


        public MeshtasticMessageStatus GetTelegramMessageStatus(long chatId, int messageId)
        {
            var cacheKey = $"TelegramMessageStatus_{chatId}_{messageId}";
            if (memoryCache.TryGetValue(cacheKey, out MeshtasticMessageStatus status))
            {
                return status;
            }
            return null;
        }

        public void StoreMeshMessageStatus(
            long meshtasticMessageId, 
            MeshtasticMessageStatus status)
        {
            var currentDelay = meshtasticService.EstimateDelay(MessagePriority.Normal);
            var cacheKey = $"MeshtasticMessageStatus_{meshtasticMessageId}";
            memoryCache.Set(cacheKey, status, currentDelay.Add(TimeSpan.FromMinutes(Math.Max(currentDelay.TotalMinutes * 1.3, 3))));
        }

        public MeshtasticMessageStatus GetMeshMessageStatus(long meshtasticMessageId)
        {
            var cacheKey = $"MeshtasticMessageStatus_{meshtasticMessageId}";
            if (memoryCache.TryGetValue(cacheKey, out MeshtasticMessageStatus status))
            {
                return status;
            }
            return null;
        }


        public void StoreChannelGateway(int channelId, long gatewayId, long deviceId, int replyHopLimit)
        {
            if (_options.DirectGatewayRoutingSeconds <= 0)
            {
                return;
            }
            var cacheKey = $"ChannelGateway_{channelId}";
            memoryCache.Set(cacheKey, new DeviceAndGatewayId
            {
                DeviceId = deviceId,
                GatewayId = gatewayId,
                ReplyHopLimit = replyHopLimit
            }, DateTime.UtcNow.AddSeconds(_options.DirectGatewayRoutingSeconds));
        }

        public void StoreDeviceGateway(MeshMessage msg)
        {
            StoreDeviceGateway(msg.DeviceId, msg.GatewayId, msg.GetSuggestedReplyHopLimit());
        }

        private void StoreDeviceGateway(long deviceId, long gatewayId, int replyHopLimit)
        {
            if (_options.DirectGatewayRoutingSeconds <= 0)
            {
                return;
            }

            var cacheKey = $"DeviceGateway_{deviceId}";
            memoryCache.Set(cacheKey, new DeviceAndGatewayId
            {
                DeviceId = deviceId,
                GatewayId = gatewayId,
                ReplyHopLimit = replyHopLimit
            }, DateTime.UtcNow.AddSeconds(_options.DirectGatewayRoutingSeconds));
        }

    }
}
