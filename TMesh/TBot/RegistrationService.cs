using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Math.EC.Rfc7748;
using TBot.Database;
using TBot.Database.Models;
using TBot.Helpers;
using TBot.Models;

namespace TBot
{
    public class RegistrationService(
        TBotDbContext db,
        IMemoryCache memoryCache,
        IOptions<TBotOptions> options,
        ILogger<RegistrationService> logger)
    {
        public const int MaxCodeVerificationTries = 5;
        private const string DeviceCachePrefix = "DeviceCache#";
        private static readonly TimeSpan DeviceCacheDuration = TimeSpan.FromHours(1);
        private readonly TBotOptions _options = options.Value;

        public async Task EnsureMigratedAsync()
        {
            try
            {
                await db.Database.MigrateAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed applying migrations");
                throw;
            }
        }

        public async Task DeleteAllDataInDB(string password)
        {
            if (password != "delete all data from the database")
            {
                throw new Exception("Invalid password");
            }

            db.DeviceRegistrations.RemoveRange(db.DeviceRegistrations);
            db.ChannelRegistrations.RemoveRange(db.ChannelRegistrations);
            db.Devices.RemoveRange(db.Devices);
            db.Channels.RemoveRange(db.Channels);
            await db.SaveChangesAsync();
        }

        public async Task<bool> HasDeviceRegistrationAsync(long chatId, long deviceId)
        {
            return await db.DeviceRegistrations.AnyAsync(r => r.ChatId == chatId && r.DeviceId == deviceId);
        }

        public async Task<bool> HasChannelRegistrationAsync(long chatId, int channelId)
        {
            return await db.ChannelRegistrations.AnyAsync(r => r.ChatId == chatId && r.ChannelId == channelId);
        }



        public void StoreDevicePendingCodeAsync(
            long telegramUserId,
            long chatId,
            long deviceId,
            int networkId,
            string code,
            DateTimeOffset expiresUtc)
        {
            memoryCache.Set(GetPendingCodeCacheKey(telegramUserId, chatId), new PendingCode
            {
                Code = code,
                NetworkId = networkId,
                Tries = 0,
                DeviceId = deviceId,
                ExpiresUtc = expiresUtc.UtcDateTime
            }, expiresUtc);
        }

        public void StoreChannelPendingCodeAsync(
            long telegramUserId,
            long chatId,
            string channelName,
            byte[] channelKey,
            int networkId,
            bool? isSingleDevice,
            string code,
            DateTimeOffset expiresUtc)
        {
            memoryCache.Set(GetPendingCodeCacheKey(telegramUserId, chatId), new PendingCode
            {
                Code = code,
                NetworkId = networkId,
                Tries = 0,
                ChannelName = channelName,
                ChannelKey = channelKey,
                IsSingleDevice = isSingleDevice,
                ExpiresUtc = expiresUtc.UtcDateTime
            }, expiresUtc);
        }

        public int GetDeviceCodesSentRecently(long deviceId)
        {
            return memoryCache.Get<int?>($"DeviceCodesSent#{deviceId}")
                ?? 0;
        }

        public int GetChannelCodesSentRecently(string channelName, string channelKey)
        {
            return memoryCache.Get<int?>($"ChannelCodesSent#{channelName}#{channelKey}")
                ?? 0;
        }

        public int GetUserCodesSentRecently(long telegramUserId)
        {
            return memoryCache.Get<int?>($"UserCodesSent#{telegramUserId}")
                ?? 0;
        }

        public int IncrementDeviceCodesSentRecently(long deviceId)
        {
            var codesSent = GetDeviceCodesSentRecently(deviceId) + 1;
            memoryCache.Set($"DeviceCodesSent#{deviceId}", codesSent, DateTimeOffset.UtcNow.AddHours(1));
            return codesSent;
        }

        public int IncrementChannelCodesSentRecently(string channelName, string channelKey)
        {
            var codesSent = GetChannelCodesSentRecently(channelName, channelKey) + 1;
            memoryCache.Set($"ChannelCodesSent#{channelName}#{channelKey}", codesSent, DateTimeOffset.UtcNow.AddHours(1));
            return codesSent;
        }

        public int IncrementUserCodesSentRecently(long telegramUserId)
        {
            var codesSent = GetUserCodesSentRecently(telegramUserId) + 1;
            memoryCache.Set($"UserCodesSent#{telegramUserId}", codesSent, DateTimeOffset.UtcNow.AddHours(1));
            return codesSent;
        }

        public void SetChatState(
            long telegramUserId,
            long chatId,
            ChatState state)
        {
            SetChatStateWithData(telegramUserId, chatId, new ChatStateWithData
            {
                State = state,
            });
        }

        public void SetChatStateWithData(
          long telegramUserId,
          long chatId,
          ChatStateWithData state)
        {
            var key = $"ChatState#{telegramUserId}#{chatId}";
            if (state.State == ChatState.Default)
            {
                memoryCache.Remove(key);
            }
            else
            {
                memoryCache.Set(key, state, DateTime.UtcNow.AddMinutes(10));
            }
        }



        public bool TryAdminLogin(long telegramUserId, string password)
        {
            var key = $"AdminPasswordTries#{telegramUserId}";
            if (!memoryCache.TryGetValue(key, out int tries))
            {
                tries = 0;
            }
            if (tries > MaxCodeVerificationTries)
            {
                return false;
            }

            tries++;
            if (password == _options.AdminPassword)
            {
                memoryCache.Remove(key);
                return true;
            }
            else
            {
                memoryCache.Set(key, tries, DateTimeOffset.UtcNow.AddHours(1));
                return false;
            }
        }

        public static string GenerateRandomCode()
        {
            return Random.Shared.Next(0, 1_000_000)
                .ToString("D6");
        }

        public ChatStateWithData GetChatState(long telegramUserId, long chatId)
        {
            var key = $"ChatState#{telegramUserId}#{chatId}";
            return memoryCache.Get<ChatStateWithData>(key);
        }

        private async Task<List<DeviceKey>> GetDeviceKeysByChatId(long chatId)
        {
            return await (from r in db.DeviceRegistrations
                          join d in db.Devices on r.DeviceId equals d.DeviceId
                          where r.ChatId == chatId
                          select new DeviceKey
                          {
                              DeviceId = r.DeviceId,
                              NetworkId = d.NetworkId,
                              PublicKey = d.PublicKey,
                          }).ToListAsync();
        }

        private async Task<List<ChannelKey>> GetChannelKeysByChatId(long chatId)
        {
            return await (from r in db.ChannelRegistrations
                          join c in db.Channels on r.ChannelId equals c.Id
                          where r.ChatId == chatId
                          select new ChannelKey
                          {
                              Id = c.Id,
                              NetworkId = c.NetworkId,
                              ChannelXor = c.XorHash,
                              PreSharedKey = c.Key,
                              IsSingleDevice = c.IsSingleDevice,
                          }).ToListAsync();
        }

        private async Task<List<ChannelKey>> GetChannelKeysByHash(int networkId, byte hash)
        {
            return await (from c in db.Channels
                          where c.NetworkId == networkId && c.XorHash == hash
                          select new ChannelKey
                          {
                              Id = c.Id,
                              NetworkId = c.NetworkId,
                              ChannelXor = c.XorHash,
                              PreSharedKey = c.Key,
                              IsSingleDevice = c.IsSingleDevice
                          }).ToListAsync();
        }

        public async Task<PublicChannel> GetNetworkPrimaryChannelCached(int networkId)
        {
            var allPrimary = await GetAllPrimaryChannelsCached();
            if (allPrimary.TryGetValue(networkId, out var primary))
            {
                return primary;
            }
            return null;
        }

        public async Task<Dictionary<int, PublicChannel>> GetAllPrimaryChannelsCached()
        {
            if (memoryCache.TryGetValue<Dictionary<int, PublicChannel>>($"PrimaryPublicChannels", out var cached))
            {
                return cached;
            }

            var res = (await db.PublicChannels
                 .AsNoTracking()
                 .Where(c => c.IsPrimary)
                 .ToListAsync())
                 .DistinctBy(c => c.NetworkId)
                 .ToDictionary(c => c.NetworkId);

            memoryCache.Set($"PrimaryPublicChannels", res, DateTimeOffset.UtcNow.AddHours(12));
            return res;
        }

        public void InvalidatePrimaryChannelsCache()
        {
            memoryCache.Remove($"PrimaryPublicChannels");
        }

        public Task<List<DeviceKey>> GetDeviceKeysByChatIdCached(long chatId)
        {
            return memoryCache.GetOrCreateAsync($"DeviceKeysByChatId#{chatId}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await GetDeviceKeysByChatId(chatId);
            });
        }

        public Task<List<ChannelKey>> GetChannelKeysByChatIdCached(long chatId)
        {
            return memoryCache.GetOrCreateAsync($"ChannelKeysByChatId#{chatId}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await GetChannelKeysByChatId(chatId);
            });
        }

        public Task<List<ChannelKey>> GetChannelKeysByHashCached(int networkId, byte hash)
        {
            return memoryCache.GetOrCreateAsync($"GetChannelKeysByHash#{networkId}#{hash}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(3);
                return await GetChannelKeysByHash(networkId, hash);
            });
        }

        public Task<ILookup<byte, PublicChannel>> GetPublicChannelKeysLookupByHashCached(int networkId)
        {
            return memoryCache.GetOrCreateAsync($"GetPublicChannelKeysLookupByHash#{networkId}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12);
                var keys = await (from c in db.PublicChannels
                                  where c.NetworkId == networkId
                                  select c).ToListAsync();
                return keys.ToLookup(k => k.XorHash);
            });
        }

        public async Task<IEnumerable<PublicChannel>> GetPublicChannelKeysByHashCached(int networkId, byte hash)
        {
            var lookup = await GetPublicChannelKeysLookupByHashCached(networkId);
            return lookup[hash];
        }

        public void InvalidateDeviceKeysByChatIdCache(long chatId)
        {
            memoryCache.Remove($"DeviceKeysByChatId#{chatId}");
        }

        public void InvalidateChannelKeysByChatIdCache(long chatId)
        {
            memoryCache.Remove($"ChannelKeysByChatId#{chatId}");
        }
        public void InvalidateChannelKeysByHashCache(int networkId, byte hash)
        {
            memoryCache.Remove($"GetChannelKeysByHash#{networkId}#{hash}");
        }

        public async Task<List<DeviceName>> GetDeviceNamesByChatId(long chatId)
        {
            return await (from r in db.DeviceRegistrations
                          join d in db.Devices on r.DeviceId equals d.DeviceId
                          where r.ChatId == chatId
                          select new DeviceName
                          {
                              DeviceId = r.DeviceId,
                              NetworkId = d.NetworkId,
                              NodeName = d.NodeName,
                              LastNodeInfo = d.UpdatedUtc,
                              LastPositionUpdate = d.LocationUpdatedUtc,
                          }).ToListAsync();
        }

        public async Task<List<ChannelName>> GetChannelNamesByChatId(long chatId)
        {
            return await (from r in db.ChannelRegistrations
                          join c in db.Channels on r.ChannelId equals c.Id
                          where r.ChatId == chatId
                          select new ChannelName
                          {
                              Id = c.Id,
                              Name = c.Name,
                              NetworkId = c.NetworkId,
                              IsSingleDevice = c.IsSingleDevice
                          }).ToListAsync();
        }

        public async Task<List<DevicePosition>> GetDevicePositionByChatId(long chatId)
        {
            return await (from r in db.DeviceRegistrations
                          join d in db.Devices on r.DeviceId equals d.DeviceId
                          where r.ChatId == chatId
                          select new DevicePosition
                          {
                              DeviceId = r.DeviceId,
                              NodeName = d.NodeName,
                              LastPositionUpdate = d.LocationUpdatedUtc,
                              Latitude = d.Latitude,
                              Longitude = d.Longitude,
                              AccuracyMeters = d.AccuracyMeters
                          }).ToListAsync();
        }



        private async Task<List<long>> GetChatsByDeviceId(long deviceId)
        {
            return await db.DeviceRegistrations
                .Where(r => r.DeviceId == deviceId)
                .Select(r => r.ChatId)
                .ToListAsync();
        }

        private async Task<List<long>> GetChatsByChannelId(long channelId)
        {
            return await db.ChannelRegistrations
                .Where(r => r.ChannelId == channelId)
                .Select(r => r.ChatId)
                .ToListAsync();
        }

        public Task<List<long>> GetChatsByDeviceIdCached(long deviceId)
        {
            return memoryCache.GetOrCreateAsync($"ChatsByDeviceId#{deviceId}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await GetChatsByDeviceId(deviceId);
            });
        }

        public Task<List<long>> GetChatsByChannelIdCached(long channelId)
        {
            return memoryCache.GetOrCreateAsync($"ChatsByChannelId#{channelId}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await GetChatsByChannelId(channelId);
            });
        }

        public void InvalidateChatsByDeviceIdCache(long deviceId)
        {
            memoryCache.Remove($"ChatsByDeviceId#{deviceId}");
        }

        public void InvalidateChatsByChannelIdCache(long channelId)
        {
            memoryCache.Remove($"ChatsByChannelId#{channelId}");
        }

        public static bool IsValidCodeFormat(string code)
        {
            if (code.Length != 6) return false;
            return code.All(c => char.IsDigit(c));
        }

        public async Task<bool> TryCreateRegistrationWithCode(
            long telegramUserId,
            long chatId,
            string code)
        {
            string key = GetPendingCodeCacheKey(telegramUserId, chatId);
            var storedCode = memoryCache.Get<PendingCode>(key);
            if (storedCode == null) return false;
            if (storedCode.ExpiresUtc < DateTime.UtcNow) return false;

            storedCode.Tries++;
            if (storedCode.Tries >= MaxCodeVerificationTries)
            {
                memoryCache.Remove(key);
                return false;
            }
            if (!string.Equals(storedCode.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                memoryCache.Set(key, storedCode, storedCode.ExpiresUtc);
                return false;
            }

            if (storedCode.DeviceId != null)
            {
                var reg = await db.DeviceRegistrations.FirstOrDefaultAsync(x =>
                    x.ChatId == chatId
                    && x.DeviceId == storedCode.DeviceId);
                var now = DateTime.UtcNow;
                if (reg == null)
                {
                    reg = new DeviceRegistration
                    {
                        TelegramUserId = telegramUserId,
                        ChatId = chatId,
                        DeviceId = storedCode.DeviceId.Value,
                        CreatedUtc = now
                    };
                    db.DeviceRegistrations.Add(reg);

                    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == storedCode.DeviceId);
                    if (device != null)
                    {
                        device.HasRegistrations = true;
                    }
                }
                else
                {
                    reg.TelegramUserId = telegramUserId;
                    reg.CreatedUtc = now;
                }
                await db.SaveChangesAsync();
                memoryCache.Remove(key);
                InvalidateDeviceKeysByChatIdCache(chatId);
                InvalidateChatsByDeviceIdCache(storedCode.DeviceId.Value);
                return true;
            }
            else if (storedCode.ChannelName != null && storedCode.ChannelKey != null)
            {
                var channel = await db.Channels.FirstOrDefaultAsync(c =>
                    c.NetworkId == storedCode.NetworkId
                    && c.Name == storedCode.ChannelName
                    && c.Key == storedCode.ChannelKey);

                var now = DateTime.UtcNow;

                if (channel == null)
                {
                    channel = new Channel
                    {
                        Name = storedCode.ChannelName,
                        Key = storedCode.ChannelKey,
                        NetworkId = storedCode.NetworkId,
                        XorHash = MeshtasticService.GenerateChannelHash(storedCode.ChannelName, storedCode.ChannelKey),
                        IsSingleDevice = storedCode.IsSingleDevice ?? false,
                        CreatedUtc = now
                    };
                    db.Channels.Add(channel);
                    await db.SaveChangesAsync();
                    InvalidateChannelKeysByHashCache(storedCode.NetworkId, channel.XorHash);
                }

                var reg = await db.ChannelRegistrations.FirstOrDefaultAsync(x =>
                    x.ChatId == chatId
                    && x.ChannelId == channel.Id);

                if (reg == null)
                {
                    reg = new ChannelRegistration
                    {
                        TelegramUserId = telegramUserId,
                        ChatId = chatId,
                        ChannelId = channel.Id,
                        CreatedUtc = now,
                    };
                    db.ChannelRegistrations.Add(reg);
                }
                else
                {
                    reg.TelegramUserId = telegramUserId;
                    reg.CreatedUtc = now;
                }
                await db.SaveChangesAsync();
                memoryCache.Remove(key);
                InvalidateChannelKeysByChatIdCache(chatId);
                InvalidateChatsByChannelIdCache(channel.Id);
                return true;
            }
            else
            {
                return false;
            }
        }

        private static string GetPendingCodeCacheKey(long telegramUserId, long chatId)
        {
            return $"PendingCode#{telegramUserId}#{chatId}";
        }

        // Device public key storage and lookup
        private static string GetDeviceCacheKey(long deviceId) => DeviceCachePrefix + deviceId;


        public async Task<Device> GetDeviceAsync(long deviceId)
        {
            if (memoryCache.TryGetValue<Device>(GetDeviceCacheKey(deviceId), out var cached))
            {
                return cached;
            }
            var entity = await db.Devices.AsNoTracking()
               .FirstOrDefaultAsync(p => p.DeviceId == deviceId);

            if (entity?.PublicKey != null)
            {
                memoryCache.Set(GetDeviceCacheKey(deviceId), entity, DeviceCacheDuration);
                return entity;
            }
            return null;
        }

        public Task<Dictionary<long, GatewayInfo>> GetGatewaysCached()
        {
            return memoryCache.GetOrCreateAsync($"GatewayNodeIds", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);
                var gatewayIds = await db.GatewayRegistrations.AsNoTracking()
                    .Select(g => new GatewayInfo
                    {
                        DeviceId = g.DeviceId,
                        LastSeen = g.LastSeenUtc,
                        NetworkId = g.NetworkId
                    }).ToListAsync();
                return gatewayIds.ToDictionary(g => g.DeviceId);
            });
        }

        public async Task RegisterGatewayAsync(long deviceId, int networkId)
        {
            var now = DateTime.UtcNow;
            var entity = await db.GatewayRegistrations.FirstOrDefaultAsync(g => g.DeviceId == deviceId);
            if (entity == null)
            {
                entity = new GatewayRegistration
                {
                    DeviceId = deviceId,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    NetworkId = networkId
                };
                db.GatewayRegistrations.Add(entity);
            }
            else
            {
                entity.UpdatedUtc = now;
            }
            await db.SaveChangesAsync();
            memoryCache.Remove("GatewayNodeIds");
        }

        public async Task<bool> UnregisterGatewayAsync(long deviceId)
        {
            var entity = await db.GatewayRegistrations.FirstOrDefaultAsync(g => g.DeviceId == deviceId);
            if (entity == null)
            {
                return false;
            }
            db.GatewayRegistrations.Remove(entity);
            await db.SaveChangesAsync();
            memoryCache.Remove("GatewayNodeIds");
            return true;
        }

        public async Task UpdateGatewayLastSeenAsync(long deviceId, DateTime lastSeen)
        {
            var entity = await db.GatewayRegistrations.FirstOrDefaultAsync(g => g.DeviceId == deviceId);
            if (entity == null)
            {
                return;
            }

            entity.LastSeenUtc = lastSeen;
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Returns gateways whose LastSeenUtc (or CreatedUtc as fallback) is older than
        /// <paramref name="olderThan"/>, removes them from the DB and invalidates cache.
        /// Returns the list of removed device IDs together with their node names for notifications.
        /// </summary>
        public async Task<List<(long DeviceId, string NodeName)>> UnregisterInactiveGatewaysAsync(DateTime olderThan)
        {
            var inactive = await db.GatewayRegistrations
                .Where(g => (g.LastSeenUtc != null ? g.LastSeenUtc : g.CreatedUtc) < olderThan)
                .ToListAsync();

            if (inactive.Count == 0)
            {
                return [];
            }

            var result = new List<(long DeviceId, string NodeName)>(inactive.Count);
            foreach (var gw in inactive)
            {
                var device = await db.Devices.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.DeviceId == gw.DeviceId);
                result.Add((gw.DeviceId, device?.NodeName ?? MeshtasticService.GetMeshtasticNodeHexId(gw.DeviceId)));
            }

            db.GatewayRegistrations.RemoveRange(inactive);
            await db.SaveChangesAsync();
            memoryCache.Remove("GatewayNodeIds");

            return result;
        }

        public async Task<Channel> GetChannelAsync(int channelId)
        {
            return await memoryCache.GetOrCreateAsync($"Channel#{channelId}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                return await db.Channels.AsNoTracking().FirstOrDefaultAsync(p => p.Id == channelId);
            });
        }

        public void InvalidateChannelCache(int channelId)
        {
            memoryCache.Remove($"Channel#{channelId}");
        }

        public async Task<Channel> FindChannelAsync(int networkId, string channelName, byte[] key)
        {
            return await db.Channels.AsNoTracking()
                .FirstOrDefaultAsync(p => p.NetworkId == networkId && p.Name == channelName && p.Key == key);
        }

        public async Task SaveAssumeChanged(Device device)
        {
            var entry = db.Devices.Attach(device);
            entry.State = EntityState.Modified;
            await db.SaveChangesAsync();
            memoryCache.Set(GetDeviceCacheKey(device.DeviceId), device, DeviceCacheDuration);
        }


        public async Task<(bool success, Device device)> SaveDeviceAsync(
            long deviceId,
            int networkId,
            string nodeName,
            byte[] publicKey)
        {
            if (publicKey == null || publicKey.Length == 0 || publicKey.Length != 32)
            {
                throw new ArgumentException("Public key must be 32 bytes", nameof(publicKey));
            }

            var entity = await db.Devices.FirstOrDefaultAsync(p => p.DeviceId == deviceId);
            var now = DateTime.UtcNow;
            if (entity == null)
            {
                entity = new Device
                {
                    DeviceId = deviceId,
                    NetworkId = networkId,
                    NodeName = nodeName,
                    PublicKey = publicKey,
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                db.Devices.Add(entity);
            }
            else if (!entity.HasRegistrations)
            {
                entity.NetworkId = networkId;
                entity.PublicKey = publicKey;
                entity.NodeName = nodeName;
                entity.UpdatedUtc = now;
            }
            else if (entity.HasRegistrations
                    && entity.PublicKey != null
                    && entity.PublicKey.AsSpan().SequenceEqual(publicKey))
            {
                entity.NetworkId = networkId;
                entity.NodeName = nodeName;
                entity.UpdatedUtc = now;
            }
            else
            {
                return (false, entity);
            }

            await db.SaveChangesAsync();
            memoryCache.Set(GetDeviceCacheKey(deviceId), entity, DeviceCacheDuration);
            return (true, entity);
        }

        public async Task<bool> HasDeviceAsync(long deviceId)
        {
            if (memoryCache.TryGetValue<Device>(GetDeviceCacheKey(deviceId), out var cachedDevice))
            {
                return true;
            }
            return await db.Devices.AnyAsync(p => p.DeviceId == deviceId);
        }

        public async Task<bool> DeleteDeviceAsync(long deviceId)
        {
            var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
            if (device == null)
            {
                return false;
            }
            var regs = await db.DeviceRegistrations
                .Where(r => r.DeviceId == deviceId)
                .ToListAsync();
            db.DeviceRegistrations.RemoveRange(regs);
            db.Devices.Remove(device);
            await db.SaveChangesAsync();
            InvalidateDeviceCache(deviceId);
            InvalidateChatsByDeviceIdCache(deviceId);
            foreach (var reg in regs)
            {
                InvalidateDeviceKeysByChatIdCache(reg.ChatId);
            }
            return true;
        }

        private void InvalidateDeviceCache(long deviceId)
        {
            memoryCache.Remove(GetDeviceCacheKey(deviceId));
        }


        // Remove all registrations for a device in a chat (any user can remove)
        public async Task<bool> RemoveDeviceFromChatAsync(long chatId, long deviceId)
        {
            var regs = await db.DeviceRegistrations
                .Where(r => r.DeviceId == deviceId)
                .ToListAsync();

            var toRemove = regs.Where(r => r.ChatId == chatId).ToList();
            if (toRemove.Count == 0)
            {
                return false;
            }
            db.DeviceRegistrations.RemoveRange(toRemove);

            var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
            if (device != null)
            {
                device.HasRegistrations = regs.Count > toRemove.Count;
            }
            await db.SaveChangesAsync();
            InvalidateDeviceCache(deviceId);
            InvalidateDeviceKeysByChatIdCache(chatId);
            InvalidateChatsByDeviceIdCache(deviceId);
            return true;
        }

        public async Task<bool> RemoveDeviceFromAllChatsViaOneChatAsync(long chatId, long deviceId)
        {
            var regs = await db.DeviceRegistrations
                .Where(r => r.DeviceId == deviceId)
                .ToListAsync();

            if (!regs.Any(r => r.ChatId == chatId))
            {
                return false;
            }

            db.DeviceRegistrations.RemoveRange(regs);

            var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
            if (device != null)
            {
                device.HasRegistrations = false;
            }
            await db.SaveChangesAsync();
            InvalidateDeviceCache(deviceId);
            InvalidateDeviceKeysByChatIdCache(chatId);
            InvalidateChatsByDeviceIdCache(deviceId);
            return true;
        }

        public async Task<(bool removedFromCurrentChat, bool removedFromOtherChats)> RemoveChannelFromAllChatsViaOneChatAsync(long chatId,
            long telegramUserId, int channelId)
        {
            var allRegs = await db.ChannelRegistrations
                .Where(r => r.ChannelId == channelId)
                .ToListAsync();

            var userOrChatRegs = allRegs.Where(r => r.TelegramUserId == telegramUserId || r.ChatId == chatId).ToList();
            if (!userOrChatRegs.Any(r => r.ChatId == chatId))
            {
                return default;
            }

            db.ChannelRegistrations.RemoveRange(userOrChatRegs);
            bool removeChannel = allRegs.Count == userOrChatRegs.Count;
            Channel channel = null;
            if (removeChannel)
            {
                channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
                db.Channels.Remove(channel);
            }
            await db.SaveChangesAsync();
            InvalidateChannelKeysByChatIdCache(chatId);
            InvalidateChatsByChannelIdCache(channelId);
            if (removeChannel)
            {
                InvalidateChannelCache(channelId);
                InvalidateChannelKeysByHashCache(channel.NetworkId, channel.XorHash);
            }
            return (true, userOrChatRegs.Count > 1);
        }

        public async Task<bool> RemoveChannelFromChat(long chatId, int channelId)
        {
            var allRegs = await db.ChannelRegistrations
                .Where(r => r.ChannelId == channelId)
                .ToListAsync();

            var chatRegs = allRegs.Where(r => r.ChatId == chatId).ToList();
            if (!chatRegs.Any(r => r.ChatId == chatId))
            {
                return false;
            }

            db.ChannelRegistrations.RemoveRange(chatRegs);

            bool removeChannel = allRegs.Count == chatRegs.Count;
            Channel channel = null;
            if (removeChannel)
            {
                channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId);
                db.Channels.Remove(channel);
            }
            await db.SaveChangesAsync();
            InvalidateChannelKeysByChatIdCache(chatId);
            InvalidateChatsByChannelIdCache(channelId);
            if (removeChannel)
            {
                InvalidateChannelCache(channelId);
                InvalidateChannelKeysByHashCache(channel.NetworkId, channel.XorHash);
            }
            return true;
        }

        public async Task<int> GetDeviceRegistrationsCountByNetwork(int networkId)
        {
            return await db.DeviceRegistrations
                .Join(db.Devices,
                    reg => reg.DeviceId,
                    dev => dev.DeviceId,
                    (reg, dev) => new { reg, dev })
                .Where(x => x.dev.NetworkId == networkId)
                .CountAsync();
        }

        public async Task<int> GetChannelRegistrationsCountByNetwork(int networkId)
        {
            return await db.ChannelRegistrations
                .Join(db.Channels,
                    reg => reg.ChannelId,
                    ch => ch.Id,
                    (reg, ch) => new { reg, ch })
                .Where(x => x.ch.NetworkId == networkId)
                .CountAsync();
        }

        public async Task<int> GetDevicesCountByNetwork(int networkId)
        {
            return await db.Devices.CountAsync(d => d.NetworkId == networkId);
        }

        public async Task<int> GetActiveDevicesCountByNetwork(int networkId, DateTime fromUtc)
        {
            return await db.Devices.CountAsync(d => d.NetworkId == networkId && d.UpdatedUtc >= fromUtc);
        }

        public async Task<Network> AddNetwork(Network network)
        {
            db.Networks.Add(network);
            await db.SaveChangesAsync();
            memoryCache.Remove($"Networks");
            memoryCache.Remove($"NetworksLookup");
            return network;
        }

        public async ValueTask<List<Network>> GetNetworksCached()
        {
            if (memoryCache.TryGetValue<List<Network>>($"Networks", out var cached))
            {
                return cached;
            }

            var networks = await db.Networks
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .ToListAsync();

            memoryCache.Set($"Networks", networks, TimeSpan.FromHours(12));
            return networks;
        }

        public async Task<bool> TryRemoveNetwork(int networkId)
        {
            var hasDevices = await db.Devices.AnyAsync(r => r.NetworkId == networkId);
            if (hasDevices)
            {
                return false;
            }

            var hasChannels = await db.Channels.AnyAsync(r => r.NetworkId == networkId);
            if (hasChannels)
            {
                return false;
            }

            var hasGatewayRegs = await db.GatewayRegistrations.AnyAsync(r => r.NetworkId == networkId);
            if (hasGatewayRegs)
            {
                return false;
            }


            var network = await db.Networks.FirstOrDefaultAsync(n => n.Id == networkId);
            if (network == null)
            {
                return true;
            }

            var publicChannels = await db.PublicChannels.Where(c => c.NetworkId == networkId).ToListAsync();
            db.PublicChannels.RemoveRange(publicChannels);
            db.Networks.Remove(network);
            await db.SaveChangesAsync();
            memoryCache.Remove($"Networks");
            memoryCache.Remove($"NetworksLookup");
            return true;
        }

        public async Task<Dictionary<int, Network>> GetNetworksLookupCached()
        {
            if (memoryCache.TryGetValue<Dictionary<int, Network>>($"NetworksLookup", out var cached))
            {
                return cached;
            }

            var networks = await db.Networks
                .ToDictionaryAsync(n => n.Id);

            memoryCache.Set($"NetworksLookup", networks, TimeSpan.FromHours(12));
            return networks;
        }

        public async Task<Network> GetNetwork(int networkId)
        {
            var networks = await GetNetworksLookupCached();
            return networks.GetValueOrDefault(networkId);
        }

        public void InvalidatePublicChannelsCache(int networkId)
        {
            memoryCache.Remove($"GetPublicChannelKeysLookupByHash#{networkId}");
            memoryCache.Remove($"PrimaryPublicChannels");
        }

        public async Task<PublicChannel> AddPublicChannelAsync(int networkId, string name, byte[] key, bool isPrimary)
        {
            var xorHash = MeshtasticService.GenerateChannelHash(name, key);
            var now = DateTime.UtcNow;
            var entity = new PublicChannel
            {
                NetworkId = networkId,
                Name = name,
                Key = key,
                XorHash = xorHash,
                IsPrimary = isPrimary,
                CreatedUtc = now
            };
            db.PublicChannels.Add(entity);
            await db.SaveChangesAsync();
            InvalidatePublicChannelsCache(networkId);
            return entity;
        }

        public async Task<(bool found, int networkId)> RemovePublicChannelAsync(int id)
        {
            var entity = await db.PublicChannels.FirstOrDefaultAsync(c => c.Id == id);
            if (entity == null) return (false, 0);
            var networkId = entity.NetworkId;
            db.PublicChannels.Remove(entity);
            await db.SaveChangesAsync();
            InvalidatePublicChannelsCache(networkId);
            return (true, networkId);
        }

        public async Task<List<PublicChannel>> GetPublicChannelsByNetworkAsync(int networkId)
        {
            return await db.PublicChannels
                .Where(c => c.NetworkId == networkId)
                .OrderBy(c => c.IsPrimary ? 0 : 1)
                .ThenBy(c => c.Name)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task<PublicChannel> GetPublicChannelByIdAsync(int id)
        {
            return await db.PublicChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        }

        public string DeriveMqttPasswordForDevice(long deviceId)
        {
            var username = MeshtasticService.GetMeshtasticNodeHexId(deviceId);
            var secret = _options.DefaultMqttPasswordDeriveSecret;
            if (_options.MqttUserPasswordDeriveSecrets != null
                && _options.MqttUserPasswordDeriveSecrets.TryGetValue(username, out var sec))
            {
                secret = sec;
            }

            return MqttPasswordDerive.DerivePassword(username, secret);
        }

        public async Task<bool> IsPublicChannel(int networkId, string channelName, byte[] key)
        {
            var networkChannels = await GetPublicChannelsByNetworkAsync(networkId);
            return networkChannels.Any(c => c.Name == channelName && c.Key.SequenceEqual(key));
        }

        // ── TgChat / Chat feature ───────────────────────────────────────────────

        public async Task<TBot.Database.Models.TgChat> GetTgChatByChatIdAsync(long chatId)
        {
            return await db.TgChats.FirstOrDefaultAsync(c => c.ChatId == chatId);
        }

        public async Task<TBot.Database.Models.TgChat> GetTgChatByHandleAsync(string handle)
        {
            if (string.IsNullOrEmpty(handle)) return null;
            var normalized = handle.TrimStart('@').ToLowerInvariant();
            return await db.TgChats
                .Where(c => c.IsActive && c.TelegramUserHandle != null)
                .FirstOrDefaultAsync(c => c.TelegramUserHandle.ToLower() == normalized);
        }

        public async Task<TBot.Database.Models.TgChat> RegisterTgChatAsync(long chatId, long telegramUserId, string handle)
        {
            var entity = await db.TgChats.FirstOrDefaultAsync(c => c.ChatId == chatId);
            var now = DateTime.UtcNow;
            if (entity == null)
            {
                entity = new TBot.Database.Models.TgChat
                {
                    ChatId = chatId,
                    TelegramUserId = telegramUserId,
                    TelegramUserHandle = NormalizeHandle(handle),
                    IsActive = true,
                    CreatedUtc = now
                };
                db.TgChats.Add(entity);
            }
            else
            {
                entity.TelegramUserId = telegramUserId;
                entity.TelegramUserHandle = NormalizeHandle(handle);
                entity.IsActive = true;
            }
            await db.SaveChangesAsync();
            memoryCache.Remove($"TgChatByHandle#{NormalizeHandle(handle)}");
            return entity;
        }

        public async Task<bool> DisableTgChatAsync(long chatId)
        {
            var entity = await db.TgChats.FirstOrDefaultAsync(c => c.ChatId == chatId);
            if (entity == null) return false;
            entity.IsActive = false;
            await db.SaveChangesAsync();
            if (entity.TelegramUserHandle != null)
                memoryCache.Remove($"TgChatByHandle#{entity.TelegramUserHandle}");
            return true;
        }

        public async Task<bool> IsDeviceApprovedForChatAsync(long tgChatId, long deviceId)
        {
            return await db.TgChatApprovedDevices.AnyAsync(a => a.TgChatId == tgChatId && a.DeviceId == deviceId);
        }

        public async Task ApproveDeviceForChatAsync(long tgChatId, long deviceId)
        {
            var exists = await db.TgChatApprovedDevices
                .AnyAsync(a => a.TgChatId == tgChatId && a.DeviceId == deviceId);
            if (!exists)
            {
                db.TgChatApprovedDevices.Add(new TBot.Database.Models.TgChatApprovedDevice
                {
                    TgChatId = tgChatId,
                    DeviceId = deviceId,
                    CreatedUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
        }

        public async Task UpdateLastChatDeviceAsync(long tgChatId, long deviceId)
        {
            var entity = await db.TgChats.FirstOrDefaultAsync(c => c.Id == tgChatId);
            if (entity != null)
            {
                entity.LastChatDeviceId = deviceId;
                entity.LastChatStartDate = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }

        public async Task<List<TgChat>> GetAllActiveTgChatsAsync()
        {
            return await db.TgChats.Where(c => c.IsActive).ToListAsync();
        }

        public async Task<List<Device>> GetAllDevicesAsync()
        {
            return await db.Devices.ToListAsync();
        }

        private static string NormalizeHandle(string handle)
        {
            if (string.IsNullOrEmpty(handle)) return null;
            return handle.TrimStart('@').ToLowerInvariant();
        }

        // ── end TgChat ──────────────────────────────────────────────────────────

        internal async Task UpdateNetworkAsync(int networkId, string newName, string newShortName, bool? newAnalytics, string newUrl, bool? newDisablePongs)
        {
            var entity = await db.Networks.FirstOrDefaultAsync(n => n.Id == networkId);
            if (entity != null)
            {
                if (newName != null)
                {
                    entity.Name = newName;
                }
                if (newShortName != null)
                {
                    entity.ShortName = newShortName;
                }
                if (newAnalytics.HasValue)
                {
                    entity.SaveAnalytics = newAnalytics.Value;
                }
                if (newUrl != null)
                {
                    entity.Url = newUrl == "-" ? null : newUrl;
                }
                if (newDisablePongs.HasValue)
                {
                    entity.DisablePongs = newDisablePongs.Value;
                }
                await db.SaveChangesAsync();
                memoryCache.Remove($"Networks");
                memoryCache.Remove($"NetworksLookup");
            }
        }
    }
}
