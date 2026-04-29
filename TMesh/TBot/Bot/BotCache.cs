using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TBot.Database;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
using TBot.Models.ChatSession;
using TBot.Models.MeshMessages;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace TBot.Bot
{
    public class BotCache
        (IMemoryCache memoryCache,
        MeshtasticService meshtasticService,
        IOptions<TBotOptions> options,
        IServiceProvider serviceProvider,
        ILogger<BotCache> logger)

    {
        private const int ChatSessionSlidingExpirationMinutes = 30;
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

        public async Task Start(IServiceScope scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<TBotDbContext>();

            var now = DateTime.UtcNow;
            var activeChatSessions = await db.ChatSessions
                .Where(cs => cs.ExpirationDate > now)
                .ToListAsync();

            logger.LogInformation("Restoring {Count} active chat sessions into cache", activeChatSessions.Count);

            foreach (var session in activeChatSessions)
            {
                var id = new DeviceOrChannelId
                {
                    DeviceId = session.DeviceId,
                    ChannelId = session.ChannelId
                };
                StoreChatSessionInCache(session.ChatId, id);
                session.ExpirationDate = now.Add(ChatSessionTtl);
            }
            await db.SaveChangesAsync();

            await db.ChatSessions.Where(cs => cs.ExpirationDate <= now)
                .ExecuteDeleteAsync();
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

        public void StoreDeviceGateway(long deviceId, long gatewayId, int replyHopLimit)
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

        private static readonly TimeSpan ChatSessionTtl = TimeSpan.FromMinutes(ChatSessionSlidingExpirationMinutes);

        public async Task StopChatSession(long chatId, TBotDbContext db)
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
                await db.ChatSessions.Where(x => x.ChatId == chatId)
                    .ExecuteDeleteAsync();
            }
        }

        public long? GetRecipientGateway(IRecipient recipient)
        {
            if (recipient.RecipientDeviceId.HasValue)
            {
                var deviceGateway = GetDeviceGateway(recipient.RecipientDeviceId.Value);
                return deviceGateway?.GatewayId;
            }
            else if (recipient.IsSingleDeviceChannel == true
                && recipient.RecipientPrivateChannelId.HasValue)
            {
                var channelGateway = GetSingleDeviceChannelGateway(recipient.RecipientPrivateChannelId.Value);
                return channelGateway?.GatewayId;
            }
            return null;
        }

        public long? GetActiveChatSessionForRecipient(IRecipient recipient)
        {
            if (recipient.RecipientDeviceId.HasValue)
            {
                return GetActiveChatSessionForDevice(recipient.RecipientDeviceId.Value);
            }
            else if (recipient.RecipientPrivateChannelId.HasValue)
            {
                return GetActiveChatSessionForChannel(recipient.RecipientPrivateChannelId.Value);
            }
            return null;
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

                using var db = scope.ServiceProvider.GetRequiredService<TBotDbContext>();
                await db.ChatSessions.Where(x => x.ChatId == chatId)
                    .ExecuteDeleteAsync();
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

        public bool TryRegisterUnknownDeviceResponse(long deviceId)
        {
            var key = $"NotRegisteredSentRecently#{deviceId}";
            if (memoryCache.TryGetValue<bool>(key, out _))
            {
                return false;
            }
            else
            {
                memoryCache.Set(key, true, TimeSpan.FromMinutes(1));
                return true;
            }
        }

        public async Task StartChatSession(long chatId, DeviceOrChannelId id, TBotDbContext db)
        {
            StoreChatSessionInCache(chatId, id);

            var chatSession = db.ChatSessions.FirstOrDefault(cs => cs.ChatId == chatId);
            if (chatSession == null)
            {
                chatSession = db.ChatSessions.Add(new ChatSession
                {
                    ChatId = chatId,
                }).Entity;
            }
            chatSession.ExpirationDate = DateTime.UtcNow.Add(ChatSessionTtl);
            chatSession.DeviceId = id.DeviceId;
            chatSession.ChannelId = id.ChannelId;
            await db.SaveChangesAsync();
        }

        private void StoreChatSessionInCache(
            long chatId,
            DeviceOrChannelId id)
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

        public async Task EndChatSessionByDeviceId(long deviceId, TBotDbContext db, long? onlyIfTgchatId = null)
        {
            var chatId = GetActiveChatSessionForDevice(deviceId);
            if (chatId != null && (!onlyIfTgchatId.HasValue || chatId == onlyIfTgchatId.Value))
            {
                await StopChatSession(chatId.Value, db);
            }
        }

        public async Task EndChatSessionByChannelId(int channelId, long? onlyIfTgchatId, TBotDbContext db)
        {
            var chatId = GetActiveChatSessionForChannel(channelId);
            if (chatId != null && (!onlyIfTgchatId.HasValue || chatId == onlyIfTgchatId.Value))
            {
                await StopChatSession(chatId.Value, db);
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

        public long? GetActiveChatSessionForRequest(DeviceOrChannelRequestCode requestCode)
        {
            if (requestCode.DeviceId != null)
            {
                return GetActiveChatSessionForDevice(requestCode.DeviceId.Value);
            }
            else if (requestCode.ChannelId != null)
            {
                return GetActiveChatSessionForChannel(requestCode.ChannelId.Value);
            }
            return null;
        }

        public long? GetActiveChatSessionForDeviceChannel(DeviceOrChannelId id)
        {
            if (id.DeviceId != null)
            {
                return GetActiveChatSessionForDevice(id.DeviceId.Value);
            }
            else if (id.ChannelId != null)
            {
                return GetActiveChatSessionForChannel(id.ChannelId.Value);
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

        public void RemovePendingChatRequest_TgToMesh(DeviceOrChannelId id)
        {
            if (id.DeviceId != null)
            {
                RemovePendingDeviceChatRequest_TgToMesh(id.DeviceId.Value);
            }
            else if (id.ChannelId != null)
            {
                RemovePendingChannelChatRequest_TgToMesh(id.ChannelId.Value);
            }
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

        public void StoreTraceRouteChat(long msgId, long chatId)
        {
            memoryCache.Set($"TraceRouteChat_{msgId}", chatId, TimeSpan.FromMinutes(10));
        }

        public long? GetTraceRouteChat(long msgId)
        {
            if (memoryCache.TryGetValue<long>($"TraceRouteChat_{msgId}", out var chatId))
            {
                return chatId;
            }
            return null;
        }

        private static string ChatSessionActive_TgChat_Key(long chatId) => $"ChatSession_TgChat_{chatId}";
        private static string ChatSessionActive_Device_Key(long deviceId) => $"ChatSession_Device_{deviceId}";
        private static string ChatSessionActive_Channel_Key(long channelId) => $"ChatSession_Channel_{channelId}";
        private static string PendingMeshToTgKey(long tgChatId) => $"PendingChatMeshToTg_{tgChatId}";
        private static string PendingDeviceTgToMeshKey(long deviceId) => $"PendingDeviceChatTgToMesh_{deviceId}";
        private static string PendingChannelTgToMeshKey(int channelId) => $"PendingChannelChatTgToMesh_{channelId}";


        public void StoreMessageSentByOurNode(long messageId)
        {
            memoryCache.Set($"SentByOurNode_{messageId}", true, TimeSpan.FromMinutes(30));
        }
        public bool IsMessageSentByOurNode(long messageId)
        {
            return memoryCache.TryGetValue<bool>($"SentByOurNode_{messageId}", out _);
        }
    }
}
