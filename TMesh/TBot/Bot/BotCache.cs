using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TBot.Helpers;
using TBot.Models;
using TBot.Models.MeshMessages;
using TBot.Models.ChatSession;
using Telegram.Bot;

namespace TBot.Bot
{
    public class BotCache
        (IMemoryCache memoryCache,
        MeshtasticService meshtasticService,
        IOptions<TBotOptions> options,
        IServiceProvider serviceProvider,
        ILogger<BotCache> logger)

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

        private static readonly TimeSpan ChatSessionTtl = TimeSpan.FromMinutes(30);

        public void StopChatSession(long chatId)
        {
            var key = ChatSessionActive_TgChat_Key(chatId);
            var session = memoryCache.Get<DeviceOrChannelId>(ChatSessionActive_TgChat_Key(chatId));
            if (session != null)
            {
                memoryCache.Remove(key);
                if (session.DeviceId != null)
                {
                    memoryCache.Remove(ChatSessionActive_Device_Key(session.DeviceId.Value));
                }
                else if (session.ChannelId != null)
                {
                    memoryCache.Remove(ChatSessionActive_Channel_Key(session.ChannelId.Value));
                }
            }
        }

        private async Task ChatSessionExpired(long chatId, DeviceOrChannelId id)
        {
            try
            {
                string key = null;
                if (id.DeviceId != null)
                {
                    key = ChatSessionActive_Device_Key(id.DeviceId.Value);
                }
                else if (id.ChannelId != null)
                {
                    key = ChatSessionActive_Channel_Key(id.ChannelId.Value);
                }
                var storedChatId = memoryCache.Get<long?>(key);
                if (storedChatId == chatId)
                {
                    memoryCache.Remove(key);
                }

                using var scope = serviceProvider.CreateScope();
                var botClient = scope.ServiceProvider.GetRequiredService<TelegramBotClient>();
                var regService = scope.ServiceProvider.GetRequiredService<RegistrationService>();

                if (id.DeviceId != null)
                {
                    var device = await regService.GetDeviceAsync(id.DeviceId.Value);
                    await botClient.TrySendMessage(regService, logger, chatId, $"Your chat session with device {device?.NodeName ?? MeshtasticService.GetMeshtasticNodeHexId(id.DeviceId.Value)} has ended.");
                }
                else
                {
                    var channel = await regService.GetChannelAsync(id.ChannelId.Value);
                    await botClient.TrySendMessage(regService, logger, chatId, $"Your chat session with channel {channel?.Name ?? "ID:" + id.ChannelId.Value.ToString()} has ended.");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in TempChatExpired callback");
            }
        }

        private async void ChatSessionExpiredCallback(object key, object value, EvictionReason reason, object state)
        {
            if (reason == EvictionReason.Expired
                  || reason == EvictionReason.Capacity)
            {
                var id = (DeviceOrChannelId)value;
                var chatId = (long)state;
                await ChatSessionExpired(chatId, id);
            }
        }

        public void StartChatSession(long chatId, DeviceOrChannelId id)
        {
            var expirationWithCallback = new MemoryCacheEntryOptions
            {
                SlidingExpiration = ChatSessionTtl,
                PostEvictionCallbacks =
                {
                    new PostEvictionCallbackRegistration
                    {
                        EvictionCallback = ChatSessionExpiredCallback,
                        State = chatId
                    }
                }
            };

            var expiration = new MemoryCacheEntryOptions
            {
                SlidingExpiration = ChatSessionTtl
            };

            memoryCache.Set(ChatSessionActive_TgChat_Key(chatId), id, expirationWithCallback);
            if (id.DeviceId != null)
            {
                memoryCache.Set(ChatSessionActive_Device_Key(id.DeviceId.Value), chatId, expiration);
            }
            else if (id.ChannelId != null)
            {
                memoryCache.Set(ChatSessionActive_Channel_Key(id.ChannelId.Value), chatId, expiration);
            }
        }

        public DeviceOrChannelId GetActiveChatSession(long chatId)
        {
            if (memoryCache.TryGetValue<DeviceOrChannelId>(ChatSessionActive_TgChat_Key(chatId), out var id))
            {
                return id;
            }
            return null;
        }

        public long? GetActiveChatSessionForDevice(long deviceId)
        {
            if (memoryCache.TryGetValue<long?>(ChatSessionActive_Device_Key(deviceId), out var tgChatId))
            {
                return tgChatId;
            }
            return null;
        }

        public long? GetActiveChatSessionForChannel(int channelId)
        {
            if (memoryCache.TryGetValue<long?>(ChatSessionActive_Channel_Key(channelId), out var tgChatId))
            {
                return tgChatId;
            }
            return null;
        }
        public void StorePendingChatRequest_MeshToTg(long tgChatId, DeviceOrChannelRequestCode requestCode)
        {
            memoryCache.Set(PendingMeshToTgKey(tgChatId), requestCode, TimeSpan.FromMinutes(10));
        }

        public DeviceOrChannelRequestCode GetPendingChatRequest_MeshToTg(long tgChatId)
        {
            if (memoryCache.TryGetValue<DeviceOrChannelRequestCode>(PendingMeshToTgKey(tgChatId), out var requestCode)) return requestCode;
            return null;
        }

        public void RemovePendingChatRequest_MeshToTg(long tgChatId)
        {
            memoryCache.Remove(PendingMeshToTgKey(tgChatId));
        }

        public void StoreDevicePendingChatRequest_TgToMesh(long deviceId, ChatRequestCode requestCode)
        {
            memoryCache.Set(PendingDeviceTgToMeshKey(deviceId), requestCode, TimeSpan.FromMinutes(5));
        }

        public void StoreChannelPendingChatRequest_TgToMesh(int channelId, ChatRequestCode requestCode)
        {
            memoryCache.Set(PendingChannelTgToMeshKey(channelId), requestCode, TimeSpan.FromMinutes(5));
        }

        public bool TryIncreaseRequestsSentCountByTgUser(long tgUserId, int maxRequests, TimeSpan interval)
        {
            if (maxRequests <= 0)
            {
                throw new ArgumentException("maxRequests must be greater than 0");
            }

            var cacheKey = $"TgUserRequestCount_{tgUserId}";
            if (memoryCache.TryGetValue<int>(cacheKey, out var res))
            {
                if (res >= maxRequests)
                {
                    return false;
                }
                res++;
                memoryCache.Set(cacheKey, res, interval);
                return true;
            }
            else
            {
                memoryCache.Set(cacheKey, 1, interval);
                return true;
            }
        }

        public ChatRequestCode GetPendingDeviceChatRequest_TgToMesh(long deviceId)
        {
            return memoryCache.TryGetValue<ChatRequestCode>(PendingDeviceTgToMeshKey(deviceId), out var requestCode) ? requestCode : null;
        }

        public void RemovePendingDeviceChatRequest_TgToMesh(long deviceId)
        {
            memoryCache.Remove(PendingDeviceTgToMeshKey(deviceId));
        }

        public ChatRequestCode GetPendingChannelChatRequest_TgToMesh(int channelId)
        {
            return memoryCache.TryGetValue<ChatRequestCode>(PendingChannelTgToMeshKey(channelId), out var requestCode) ? requestCode : null;
        }

        public void RemovePendingChannelChatRequest_TgToMesh(int channelId)
        {
            memoryCache.Remove(PendingChannelTgToMeshKey(channelId));
        }

        private static string ChatSessionActive_TgChat_Key(long chatId) => $"ChatSession_TgChat_{chatId}";
        private static string ChatSessionActive_Device_Key(long deviceId) => $"ChatSession_Device_{deviceId}";
        private static string ChatSessionActive_Channel_Key(long channelId) => $"ChatSession_Channel_{channelId}";
        private static string PendingMeshToTgKey(long tgChatId) => $"PendingChatMeshToTg_{tgChatId}";
        private static string PendingDeviceTgToMeshKey(long deviceId) => $"PendingDeviceChatTgToMesh_{deviceId}";
        private static string PendingChannelTgToMeshKey(int channelId) => $"PendingChannelChatTgToMesh_{channelId}";

    }
}
