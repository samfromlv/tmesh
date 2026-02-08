using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TBot.Database;
using TBot.Database.Models;
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

        public async Task<bool> HasRegistrationAsync(long chatId, long deviceId)
        {
            return await db.Registrations.AnyAsync(r => r.ChatId == chatId && r.DeviceId == deviceId);
        }

        public void StorePendingCodeAsync(
            long telegramUserId,
            long chatId,
            long deviceId,
            string code,
            DateTimeOffset expiresUtc)
        {
            memoryCache.Set(GetPendingCodeCacheKey(telegramUserId, chatId), new PendingCode
            {
                Code = code,
                Tries = 0,
                DeviceId = deviceId,
                ExpiresUtc = expiresUtc.UtcDateTime
            }, expiresUtc);
        }

        public int GetDeviceCodesSentRecently(long deviceId)
        {
            return memoryCache.Get<int?>($"DeviceCodesSent#{deviceId}")
                ?? 0;
        }

        public int IncrementDeviceCodesSentRecently(long deviceId)
        {
            var codesSent = GetDeviceCodesSentRecently(deviceId) + 1;
            memoryCache.Set($"DeviceCodesSent#{deviceId}", codesSent, DateTimeOffset.UtcNow.AddHours(1));
            return codesSent;
        }

        public void SetChatState(
            long telegramUserId,
            long chatId,
            ChatState state)
        {
            var key = $"ChatState#{telegramUserId}#{chatId}";
            if (state == ChatState.Default)
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

        public ChatState? GetChatState(long telegramUserId, long chatId)
        {
            var key = $"ChatState#{telegramUserId}#{chatId}";
            return memoryCache.Get<ChatState?>(key);
        }

        private async Task<List<DeviceKey>> GetDeviceKeysByChatId(long chatId)
        {
            return await (from r in db.Registrations
                          join d in db.Devices on r.DeviceId equals d.DeviceId
                          where r.ChatId == chatId
                          select new DeviceKey
                          {
                              DeviceId = r.DeviceId,
                              PublicKey = d.PublicKey,
                          }).ToListAsync();
        }

        public Task<List<DeviceKey>> GetDeviceKeysByChatIdCached(long chatId)
        {
            return memoryCache.GetOrCreateAsync($"DeviceKeysByChatId#{chatId}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await GetDeviceKeysByChatId(chatId);
            });
        }

        public void InvalidateDeviceKeysByChatIdCache(long chatId)
        {
            memoryCache.Remove($"DeviceKeysByChatId#{chatId}");
        }

        public async Task<List<DeviceName>> GetDeviceNamesByChatId(long chatId)
        {
            return await (from r in db.Registrations
                          join d in db.Devices on r.DeviceId equals d.DeviceId
                          where r.ChatId == chatId
                          select new DeviceName
                          {
                              DeviceId = r.DeviceId,
                              NodeName = d.NodeName,
                              LastNodeInfo = d.UpdatedUtc,
                              LastPositionUpdate = d.LocationUpdatedUtc,
                          }).ToListAsync();
        }

        public async Task<List<DevicePosition>> GetDevicePositionByChatId(long chatId)
        {
            return await (from r in db.Registrations
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
            return await db.Registrations
                .Where(r => r.DeviceId == deviceId)
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

        public void InvalidateChatsByDeviceIdCache(long deviceId)
        {
            memoryCache.Remove($"ChatsByDeviceId#{deviceId}");
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

            var reg = await db.Registrations.FirstOrDefaultAsync(x =>
                x.ChatId == chatId
                && x.DeviceId == storedCode.DeviceId);
            var now = DateTime.UtcNow;
            if (reg == null)
            {
                reg = new DeviceRegistration
                {
                    TelegramUserId = telegramUserId,
                    ChatId = chatId,
                    DeviceId = storedCode.DeviceId,
                    CreatedUtc = now
                };
                db.Registrations.Add(reg);

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
            InvalidateChatsByDeviceIdCache(storedCode.DeviceId);
            return true;
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

        public async Task SaveAssumeChanged(Device device)
        {
            var entry = db.Devices.Attach(device);
            entry.State = EntityState.Modified;
            await db.SaveChangesAsync();
            memoryCache.Set(GetDeviceCacheKey(device.DeviceId), device, DeviceCacheDuration);
        }


        public async Task<bool> SaveDeviceAsync(
            long deviceId,
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
                    NodeName = nodeName,
                    PublicKey = publicKey,
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                db.Devices.Add(entity);
            }
            else if (!entity.HasRegistrations)
            {
                entity.PublicKey = publicKey;
                entity.NodeName = nodeName;
                entity.UpdatedUtc = now;
            }
            else if (entity.HasRegistrations
                    && entity.PublicKey != null
                    && entity.PublicKey.AsSpan().SequenceEqual(publicKey))
            {
                entity.NodeName = nodeName;
                entity.UpdatedUtc = now;
            }
            else
            {
                return false;
            }

            await db.SaveChangesAsync();
            memoryCache.Set(GetDeviceCacheKey(deviceId), entity, DeviceCacheDuration);
            return true;
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
            var regs = await db.Registrations
                .Where(r => r.DeviceId == deviceId)
                .ToListAsync();
            db.Registrations.RemoveRange(regs);
            db.Devices.Remove(device);
            await db.SaveChangesAsync();
            memoryCache.Remove(GetDeviceCacheKey(deviceId));
            InvalidateChatsByDeviceIdCache(deviceId);
            foreach (var reg in regs)
            {
                InvalidateDeviceKeysByChatIdCache(reg.ChatId);
            }
            return true;
        }


        // Remove all registrations for a device in a chat (any user can remove)
        public async Task<bool> RemoveDeviceFromChatAsync(long chatId, long deviceId)
        {
            var regs = await db.Registrations
                .Where(r => r.DeviceId == deviceId)
                .ToListAsync();

            var toRemove = regs.Where(r => r.ChatId == chatId).ToList();
            if (toRemove.Count == 0)
            {
                return false;
            }
            db.Registrations.RemoveRange(toRemove);

            var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
            if (device != null)
            {
                device.HasRegistrations = regs.Count > toRemove.Count;
            }
            await db.SaveChangesAsync();
            memoryCache.Remove(GetDeviceCacheKey(deviceId));
            InvalidateDeviceKeysByChatIdCache(chatId);
            InvalidateChatsByDeviceIdCache(deviceId);
            return true;
        }

        public async Task<bool> RemoveDeviceFromAllChatsViaOneChatAsync(long chatId, long deviceId)
        {
            var regs = await db.Registrations
                .Where(r => r.DeviceId == deviceId)
                .ToListAsync();

            if (!regs.Any(r => r.ChatId == chatId))
            {
                return false;
            }

            db.Registrations.RemoveRange(regs);

            var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
            if (device != null)
            {
                device.HasRegistrations = false;
            }
            await db.SaveChangesAsync();
            memoryCache.Remove(GetDeviceCacheKey(deviceId));
            InvalidateDeviceKeysByChatIdCache(chatId);
            InvalidateChatsByDeviceIdCache(deviceId);
            return true;
        }

        public async Task<int> GetTotalRegistrationsCount()
        {
            return await db.Registrations.CountAsync();
        }

        public async Task<int> GetTotalDevicesCount()
        {
            return await db.Devices.CountAsync();
        }

        public async Task<int> GetActiveDevicesCount(DateTime fromUtc)
        {
            return await db.Devices.CountAsync(d => d.UpdatedUtc >= fromUtc);
        }
    }
}
