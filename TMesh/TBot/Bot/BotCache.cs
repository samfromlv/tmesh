using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using TBot.Database;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;
using TBot.Models.Admin;
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
        private const int ChatSessionRefreshAfterMinutes = ChatSessionSlidingExpirationMinutes - 10;
        private readonly TBotOptions _options = options.Value;
        private static readonly object _globalChannelSessionLock = new object();


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
                    ChannelId = session.ChannelId,
                    PublicChannelId = session.PublicChannelId,
                    ForceGatewayId = session.ForceGatewayId,
                    ImpersonateDeviceId = session.ImpersonateDeviceId
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

        public void StoreTelegramMessageStatus(
         int networkId,
         long chatId,
         int messageId,
         MeshtasticMessageStatus status)
        {
            var currentMeshQueueDelay = meshtasticService.EstimateDelay(networkId, MessagePriority.Normal);

            var cacheKey = $"TelegramMessageStatus_{chatId}_{messageId}";
            memoryCache.Set(cacheKey, status, currentMeshQueueDelay.Add(TimeSpan.FromMinutes(Math.Max(currentMeshQueueDelay.TotalMinutes * 1.3, 10))));
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

        public void StorePingStatus(long meshtasticMessageId, PingInfo pingInfo, int slidingExpirationSeconds)
        {
            var cacheKey = $"PingStatus_{meshtasticMessageId}";
            memoryCache.Set(cacheKey, pingInfo, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromSeconds(slidingExpirationSeconds)
            });
        }


        public PingInfo GetPingStatus(long meshtasticMessageId)
        {
            var cacheKey = $"PingStatus_{meshtasticMessageId}";
            if (memoryCache.TryGetValue(cacheKey, out PingInfo status))
            {
                return status;
            }
            return null;
        }

        public void RemovePingStatus(long meshtasticMessageId)
        {
            var cacheKey = $"PingStatus_{meshtasticMessageId}";
            memoryCache.Remove(cacheKey);
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
            if (msg.TMeshGatewayId.HasValue)
            {
                StoreDeviceGateway(msg.DeviceId, msg.TMeshGatewayId.Value, msg.GetSuggestedReplyHopLimit());
            }
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
                else if (session.ChannelId != null || session.PublicChannelId != null)
                {
                    var chKey = session.ChannelId != null 
                        ? ChatSessionActive_Channel_Key(session.ChannelId.Value) 
                        : ChatSessionActive_PublicChannel_Key(session.PublicChannelId.Value);

                    lock (_globalChannelSessionLock)
                    {
                        var currentChatIds = memoryCache.Get<List<long>>(chKey);
                        if (currentChatIds != null)
                        {
                            var updatedChatIds = new List<long>(currentChatIds.Where(id => id != chatId));
                            if (updatedChatIds == null)
                            {
                                memoryCache.Set(chKey, updatedChatIds);
                            }
                            else
                            {
                                memoryCache.Remove(chKey);
                            }
                        }
                    }
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

        public async ValueTask<List<long>> GetActiveChatSessionsForRecipient(IRecipient recipient, TBotDbContext db)
        {
            if (recipient.RecipientDeviceId.HasValue)
            {
                var chatId = await GetActiveChatSessionForDevice(recipient.RecipientDeviceId.Value, db);
                return chatId.HasValue ? [chatId.Value] : null;
            }
            else if (recipient.RecipientPrivateChannelId.HasValue)
            {
                return await GetActiveChatSessionsForChannel(recipient.RecipientPrivateChannelId.Value, db);
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
                    var storedChatId = memoryCache.Get<long?>(key);
                    if (storedChatId == chatId)
                    {
                        memoryCache.Remove(key);
                    }
                }
                else if (id.ChannelId != null || id.PublicChannelId != null)
                {
                    key = id.ChannelId != null
                        ? ChatSessionActive_Channel_Key(id.ChannelId.Value)
                        : ChatSessionActive_PublicChannel_Key(id.PublicChannelId.Value);

                    lock (_globalChannelSessionLock)
                    {
                        var currentChatIds = memoryCache.Get<List<long>>(key);
                        if (currentChatIds != null)
                        {
                            var updatedChatIds = new List<long>(currentChatIds.Where(id => id != chatId));
                            if (updatedChatIds.Count > 0)
                            {
                                memoryCache.Set(key, updatedChatIds);
                            }
                            else
                            {
                                memoryCache.Remove(key);
                            }
                        }
                    }
                }
               

                using var scope = serviceProvider.CreateScope();
                var botClient = scope.ServiceProvider.GetRequiredService<TelegramBotClient>();
                var regService = scope.ServiceProvider.GetRequiredService<RegistrationService>();

                if (id.DeviceId != null)
                {
                    var device = await regService.GetDeviceAsync(id.DeviceId.Value);
                    await botClient.TrySendMessage(regService, logger, chatId, $"Your chat session with device {device?.NodeName ?? MeshtasticService.GetMeshtasticNodeHexId(id.DeviceId.Value)} has ended.");
                }
                else if (id.ChannelId != null)
                {
                    var channel = await regService.GetChannelAsync(id.ChannelId.Value);
                    await botClient.TrySendMessage(regService, logger, chatId, $"Your chat session with channel {channel?.Name ?? "ID:" + id.ChannelId.Value.ToString()} has ended.");
                }
                else if (id.PublicChannelId != null)
                {
                    var channel = await regService.GetPublicChannelByIdCachedAsync(id.PublicChannelId.Value);
                    await botClient.TrySendMessage(regService, logger, chatId, $"Your chat session with public channel {channel?.Name ?? "ID:" + id.PublicChannelId.Value.ToString()} has ended.");
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
            chatSession ??= db.ChatSessions.Add(new ChatSession
            {
                ChatId = chatId,
            }).Entity;
            chatSession.ExpirationDate = DateTime.UtcNow.Add(ChatSessionTtl);
            chatSession.DeviceId = id.DeviceId;
            chatSession.ChannelId = id.ChannelId;
            chatSession.PublicChannelId = id.PublicChannelId;
            chatSession.ImpersonateDeviceId = id.ImpersonateDeviceId;
            chatSession.ForceGatewayId = id.ForceGatewayId;
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

            id.LastRefreshed = DateTime.UtcNow;

            memoryCache.Set(ChatSessionActive_TgChat_Key(chatId), id, expirationWithCallback);
            if (id.DeviceId != null)
            {
                memoryCache.Set(ChatSessionActive_Device_Key(id.DeviceId.Value), chatId, expiration);
            }
            else if (id.ChannelId != null || id.PublicChannelId != null)
            {
                var key = id.ChannelId != null 
                    ? ChatSessionActive_Channel_Key(id.ChannelId.Value)
                    : ChatSessionActive_PublicChannel_Key(id.PublicChannelId.Value);

                lock (_globalChannelSessionLock)
                {
                    var currentChatIds = memoryCache.Get<List<long>>(key);
                    if (currentChatIds == null)
                    {
                        currentChatIds = new List<long>();
                    }
                    if (!currentChatIds.Contains(chatId))
                    {
                        currentChatIds = new List<long>(currentChatIds) { chatId };
                    }
                    memoryCache.Set(key, currentChatIds, expiration);
                }
            }
        }


        public async Task EndChatSessionByDeviceId(long deviceId, TBotDbContext db, long? onlyIfTgchatId = null)
        {
            var chatId = await GetActiveChatSessionForDevice(deviceId, db);
            if (chatId != null && (!onlyIfTgchatId.HasValue || chatId == onlyIfTgchatId.Value))
            {
                await StopChatSession(chatId.Value, db);
            }
        }

        public async Task EndChatSessionByChannelId(int channelId, long? onlyIfTgchatId, TBotDbContext db)
        {
            var chatIds = await GetActiveChatSessionsForChannel(channelId, db);
            if (chatIds != null)
            {
                foreach (var chatId in chatIds.Where(chatId => !onlyIfTgchatId.HasValue || chatId == onlyIfTgchatId.Value))
                {
                    await StopChatSession(chatId, db);
                }
            }
        }

        public async ValueTask<DeviceOrChannelId> GetActiveChatSession(long chatId, TBotDbContext db)
        {
            if (memoryCache.TryGetValue<DeviceOrChannelId>(ChatSessionActive_TgChat_Key(chatId), out var id))
            {
                if (id != null)
                {
                    var now = DateTime.UtcNow;
                    var sinceRefreshed = now - id.LastRefreshed;
                    if (sinceRefreshed.TotalMinutes >= ChatSessionRefreshAfterMinutes)
                    {
                        id.LastRefreshed = now;
                        var newExpiration = now.Add(ChatSessionTtl);
                        await db.ChatSessions.Where(x => x.ChatId == chatId)
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(x => x.ExpirationDate, newExpiration)
                            );
                    }
                }
                return id;
            }
            return null;
        }

        public async ValueTask<long?> GetActiveChatSessionForDevice(long deviceId, TBotDbContext db)
        {
            if (memoryCache.TryGetValue<long?>(ChatSessionActive_Device_Key(deviceId), out var tgChatId))
            {
                if (tgChatId.HasValue)
                {
                    await GetActiveChatSession(tgChatId.Value, db);
                }
                return tgChatId;
            }
            return null;
        }

        public async ValueTask<List<long>> GetActiveChatSessionsForChannel(int channelId, TBotDbContext db)
        {
            if (memoryCache.TryGetValue<List<long>>(ChatSessionActive_Channel_Key(channelId), out var tgChatIds))
            {
                if (tgChatIds != null)
                {
                    foreach (var tgChatId in tgChatIds)
                    {
                        await GetActiveChatSession(tgChatId, db);
                    }
                }
                return tgChatIds;
            }
            return null;
        }

        public async ValueTask<List<long>> GetActiveChatSessionsForPublicChannel(int publicChannelId, TBotDbContext db)
        {
            if (memoryCache.TryGetValue<List<long>>(ChatSessionActive_PublicChannel_Key(publicChannelId), out var tgChatIds))
            {
                if (tgChatIds != null)
                {
                    foreach (var tgChatId in tgChatIds)
                    {
                        await GetActiveChatSession(tgChatId, db);
                    }
                }
                return tgChatIds;
            }
            return null;
        }

        public async ValueTask<List<long>> GetActiveChatSessionsForRequest(DeviceOrChannelRequestCode requestCode, TBotDbContext db)
        {
            if (requestCode.DeviceId != null)
            {
                var chatId = await GetActiveChatSessionForDevice(requestCode.DeviceId.Value, db);
                return chatId.HasValue ? new List<long> { chatId.Value } : null;
            }
            else if (requestCode.ChannelId != null)
            {
                return await GetActiveChatSessionsForChannel(requestCode.ChannelId.Value, db);
            }

            return null;
        }

        public async ValueTask<List<long>> GetActiveChatSessionForDeviceChannel(DeviceOrChannelId id, TBotDbContext db)
        {
            if (id.DeviceId != null)
            {
                var chatId = await GetActiveChatSessionForDevice(id.DeviceId.Value, db);
                return chatId.HasValue ? [chatId.Value] : null;
            }
            else if (id.ChannelId != null)
            {
                return await GetActiveChatSessionsForChannel(id.ChannelId.Value, db);
            }
            else if (id.PublicChannelId != null)
            {
                return await GetActiveChatSessionsForPublicChannel(id.PublicChannelId.Value, db);
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
            //else if (id.PublicChannelId != null)
            //{
            //    // No separate pending request for public channels, so nothing to remove
            //}
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
        private static string ChatSessionActive_PublicChannel_Key(long channelId) => $"ChatSession_PublicChannel_{channelId}";
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

        public void StoreMassDirectMessage(string code, MassDirectMessage msg)
        {
            var key = $"MassDirectMessage_{code}";
            memoryCache.Set(key, msg, TimeSpan.FromMinutes(10));
        }

        public MassDirectMessage GetMassDirectMessage(string code)
        {
            var key = $"MassDirectMessage_{code}";
            if (memoryCache.TryGetValue<MassDirectMessage>(key, out var msg))
            {
                return msg;
            }
            return null;
        }
    }
}
