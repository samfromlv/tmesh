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
         int networkId,
         long chatId,
         int messageId,
         MeshtasticMessageStatus status)
        {
            var currentMeshQueueDelay = meshtasticService.EstimateDelay(networkId, MessagePriority.Normal);

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
            int networkId,
            long meshtasticMessageId,
            MeshtasticMessageStatus status)
        {
            var currentDelay = meshtasticService.EstimateDelay(networkId, MessagePriority.Normal);
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

        // ── Active chat sessions ──────────────────────────────────────────────

        private static readonly TimeSpan ChatSessionTtl = TimeSpan.FromHours(1);

        public void StoreActiveMeshChat(long chatId, long deviceId)
        {
            memoryCache.Set(ActiveChatKey(chatId, deviceId), true, ChatSessionTtl);
        }

        public bool HasActiveMeshChat(long chatId, long deviceId)
        {
            return memoryCache.TryGetValue(ActiveChatKey(chatId, deviceId), out _);
        }

        public void RemoveActiveMeshChat(long chatId, long deviceId)
        {
            memoryCache.Remove(ActiveChatKey(chatId, deviceId));
        }

        public void AddActiveChatDevice(long chatId, long deviceId)
        {
            StoreActiveMeshChat(chatId, deviceId);
            var listKey = ActiveChatListKey(chatId);
            var list = memoryCache.Get<HashSet<long>>(listKey) ?? new HashSet<long>();
            list.Add(deviceId);
            memoryCache.Set(listKey, list, ChatSessionTtl);
        }

        public void RemoveActiveChatDevice(long chatId, long deviceId)
        {
            RemoveActiveMeshChat(chatId, deviceId);
            var listKey = ActiveChatListKey(chatId);
            if (memoryCache.TryGetValue<HashSet<long>>(listKey, out var list))
            {
                list.Remove(deviceId);
                if (list.Count > 0)
                    memoryCache.Set(listKey, list, ChatSessionTtl);
                else
                    memoryCache.Remove(listKey);
            }
        }

        public HashSet<long> GetActiveChatDevices(long chatId)
        {
            return memoryCache.Get<HashSet<long>>(ActiveChatListKey(chatId)) ?? new HashSet<long>();
        }

        public void StorePendingChatRequest_MeshToTg(long deviceId, long tgChatId)
        {
            memoryCache.Set(PendingMeshToTgKey(deviceId), tgChatId, TimeSpan.FromMinutes(10));
            memoryCache.Set(PendingMeshToTgReverseKey(tgChatId), deviceId, TimeSpan.FromMinutes(10));
        }

        public long? GetPendingChatRequest_MeshToTg(long deviceId)
        {
            if (memoryCache.TryGetValue<long>(PendingMeshToTgKey(deviceId), out var v)) return v;
            return null;
        }

        public long? GetPendingChatRequest_MeshToTg_ByChatId(long chatId)
        {
            if (memoryCache.TryGetValue<long>(PendingMeshToTgReverseKey(chatId), out var deviceId)) return deviceId;
            return null;
        }

        public void RemovePendingChatRequest_MeshToTg(long deviceId)
        {
            if (memoryCache.TryGetValue<long>(PendingMeshToTgKey(deviceId), out var chatId))
            {
                memoryCache.Remove(PendingMeshToTgReverseKey(chatId));
            }
            memoryCache.Remove(PendingMeshToTgKey(deviceId));
        }

        public void StorePendingChatRequest_TgToMesh(long chatId, long deviceId)
        {
            memoryCache.Set(PendingTgToMeshKey(chatId, deviceId), true, TimeSpan.FromMinutes(10));
            memoryCache.Set(PendingTgToMeshReverseKey(chatId), deviceId, TimeSpan.FromMinutes(10));
        }

        public bool HasPendingChatRequest_TgToMesh(long chatId, long deviceId)
        {
            return memoryCache.TryGetValue(PendingTgToMeshKey(chatId, deviceId), out _);
        }

        public long? GetPendingChatRequest_TgToMesh(long chatId)
        {
            if (memoryCache.TryGetValue<long>(PendingTgToMeshReverseKey(chatId), out var deviceId)) return deviceId;
            return null;
        }

        public void RemovePendingChatRequest_TgToMesh(long chatId, long deviceId)
        {
            memoryCache.Remove(PendingTgToMeshKey(chatId, deviceId));
            memoryCache.Remove(PendingTgToMeshReverseKey(chatId));
        }

        // ── end active chat sessions ──────────────────────────────────────────

        private static string ActiveChatKey(long chatId, long deviceId) => $"ActiveChat_{chatId}_{deviceId}";
        private static string ActiveChatListKey(long chatId) => $"ActiveChatList_{chatId}";
        private static string PendingMeshToTgKey(long deviceId) => $"PendingChatMeshToTg_{deviceId}";
        private static string PendingMeshToTgReverseKey(long chatId) => $"PendingChatMeshToTgReverse_{chatId}";
        private static string PendingTgToMeshKey(long chatId, long deviceId) => $"PendingChatTgToMesh_{chatId}_{deviceId}";
        private static string PendingTgToMeshReverseKey(long chatId) => $"PendingChatTgToMeshReverse_{chatId}";

    }
}
