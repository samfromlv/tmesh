using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TBot.Database;
using TBot.Database.Models;
using TBot.Models;

namespace TBot
{
    public class RegistrationService
    {
        public const int MaxCodeVerificationTries = 5;
        private const string DevicePublicKeyCachePrefix = "DevicePublicKey#";
        private static readonly TimeSpan DevicePublicKeyCacheDuration = TimeSpan.FromHours(1);

        private readonly TBotDbContext _db;
        private readonly ILogger<RegistrationService> _logger;
        private readonly IMemoryCache _memoryCache;

        public RegistrationService(
            TBotDbContext db,
            IMemoryCache memoryCache,
            ILogger<RegistrationService> logger)
        {
            _db = db;
            _logger = logger;
            _memoryCache = memoryCache;
        }

        public async Task EnsureMigratedAsync()
        {
            try
            {
                await _db.Database.MigrateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed applying migrations");
                throw;
            }
        }

        public async Task<bool> HasRegistrationAsync(long chatId, long deviceId)
        {
            return await _db.Registrations.AnyAsync(r => r.ChatId == chatId && r.DeviceId == deviceId);
        }

        public void StorePendingCodeAsync(
            long telegramUserId,
            long chatId,
            long deviceId,
            string code,
            DateTimeOffset expiresUtc)
        {
            _memoryCache.Set(GetPendingCodeCacheKey(telegramUserId, chatId), new PendingCode
            {
                Code = code,
                Tries = 0,
                DeviceId = deviceId,
                ExpiresUtc = expiresUtc.UtcDateTime
            }, expiresUtc);
        }

        public int GetDeviceCodesSentRecently(long deviceId)
        {
            return _memoryCache.Get<int?>($"DeviceCodesSent#{deviceId}")
                ?? 0;
        }

        public int IncrementDeviceCodesSentRecently(long deviceId)
        {
            var codesSent = GetDeviceCodesSentRecently(deviceId) + 1;
            _memoryCache.Set($"DeviceCodesSent#{deviceId}", codesSent, DateTimeOffset.UtcNow.AddHours(1));
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
                _memoryCache.Remove(key);
            }
            else
            {
                _memoryCache.Set(key, state, DateTime.UtcNow.AddMinutes(10));
            }
        }

        public string GenerateRandomCode()
        {
            return Random.Shared.Next(0, 1_000_000)
                .ToString("D6");
        }

        public ChatState? GetChatState(long telegramUserId, long chatId)
        {
            var key = $"ChatState#{telegramUserId}#{chatId}";
            return _memoryCache.Get<ChatState?>(key);
        }

        public async Task<List<DeviceWithNameAndKey>> GetDevicesByChatId(long chatId)
        {
            return await (from r in _db.Registrations
                         join d in _db.Devices on r.DeviceId equals d.DeviceId
                         where r.ChatId == chatId
                         select new DeviceWithNameAndKey
                         {
                             DeviceId = r.DeviceId,
                             PublicKey = d.PublicKey,
                             NodeName = d.NodeName,
                         }).ToListAsync();
        }

        public async Task<List<DeviceRegistration>> GetRegistrationsByDeviceId(long deviceId)
        {
            return await _db.Registrations
                .Where(r => r.DeviceId == deviceId)
                .ToListAsync();
        }

        public bool IsValidCodeFormat(string code)
        {
            if (code.Length != 6) return false;
            return code.All(c => char.IsDigit(c));
        }

        public async Task<bool> TryCreateRegistrationWithCode(
            long telegramUserId,
            string userName,
            long chatId,
            string code)
        {
            string key = GetPendingCodeCacheKey(telegramUserId, chatId);
            var storedCode = _memoryCache.Get<PendingCode>(key);
            if (storedCode == null) return false;
            if (storedCode.ExpiresUtc < DateTime.UtcNow) return false;

            storedCode.Tries++;
            if (storedCode.Tries >= MaxCodeVerificationTries)
            {
                _memoryCache.Remove(key);
                return false;
            }
            if (!string.Equals(storedCode.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                _memoryCache.Set(key, storedCode, storedCode.ExpiresUtc);
                return false;
            }

            var reg = await _db.Registrations.FirstOrDefaultAsync(x =>
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
                _db.Registrations.Add(reg);
            }
            else
            {
                reg.TelegramUserId = telegramUserId;
                reg.CreatedUtc = now;
            }
            await _db.SaveChangesAsync();
            _memoryCache.Remove(key);
            return true;
        }

        private static string GetPendingCodeCacheKey(long telegramUserId, long chatId)
        {
            return $"PendingCode#{telegramUserId}#{chatId}";
        }

        // Device public key storage and lookup
        private string GetDeviceCacheKey(long deviceId) => DevicePublicKeyCachePrefix + deviceId;

        public async Task<Device> GetDeviceAsync(long deviceId)
        {
            if (_memoryCache.TryGetValue<Device>(GetDeviceCacheKey(deviceId), out var cached))
            {
                return cached;
            }

            var entity = await _db.Devices.AsNoTracking()
                .FirstOrDefaultAsync(p => p.DeviceId == deviceId);

            if (entity?.PublicKey != null)
            {
                _memoryCache.Set(GetDeviceCacheKey(deviceId), entity, DevicePublicKeyCacheDuration);
                return entity;
            }
            return null;
        }

        public async Task SetDeviceAsync(long deviceId, string nodeName, byte[] publicKey)
        {
            if (publicKey == null || publicKey.Length == 0 || publicKey.Length != 32)
            {
                throw new ArgumentException("Public key must be 32 bytes", nameof(publicKey));
            }

            var entity = await _db.Devices.FirstOrDefaultAsync(p => p.DeviceId == deviceId);
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
                _db.Devices.Add(entity);
            }
            else
            {
                entity.PublicKey = publicKey;
                entity.NodeName = nodeName;
                entity.UpdatedUtc = now;
            }

            await _db.SaveChangesAsync();
            _memoryCache.Set(GetDeviceCacheKey(deviceId), entity, DevicePublicKeyCacheDuration);
        }

        public async Task<bool> HasDeviceAsync(long deviceId)
        {
            if (_memoryCache.TryGetValue<Device>(GetDeviceCacheKey(deviceId), out var cachedDevice))
            {
                return true;
            }
            return await _db.Devices.AnyAsync(p => p.DeviceId == deviceId);
        }

        // Remove all registrations for a device in a chat (any user can remove)
        public async Task<bool> RemoveDeviceFromChatAsync(long chatId, long deviceId)
        {
            var regs = await _db.Registrations
                .Where(r => r.ChatId == chatId && r.DeviceId == deviceId)
                .ToListAsync();
            if (regs.Count == 0)
            {
                return false;
            }
            _db.Registrations.RemoveRange(regs);
            await _db.SaveChangesAsync();
            return true;
        }
    }
}
