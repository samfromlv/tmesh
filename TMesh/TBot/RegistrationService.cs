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
            _memoryCache.Set($"PendingCode{telegramUserId}#{chatId}", new PendingCode
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

        public async Task<List<DeviceRegistration>> GetRegistrationsAsync(long chatId)
        {
            return await _db.Registrations
                .Where(r => r.ChatId == chatId)
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
            string key = $"PendingCode#{telegramUserId}#{chatId}";
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

            var reg = await _db.Registrations.FirstOrDefaultAsync(x => x.ChatId == chatId && x.DeviceId == deviceId);
            if (reg == null)
            {
                reg = new DeviceRegistration
                {
                    TelegramUserId = telegramUserId,
                    ChatId = chatId,
                    DeviceId = storedCode.DeviceId,
                    CreatedUtc = DateTime.UtcNow,
                    UserName = userName
                };
                _db.Registrations.Add(reg);
            }
            else
            {
                reg.UserName = userName;
                reg.TelegramUserId = telegramUserId;
                reg.CreatedUtc = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
            _memoryCache.Remove(key);
            return true;
        }

        public async Task<bool> TryVerifyAnyAsync(long telegramUserId, string code)
        {
            var pendings = await _db.PendingCodes.Where(p => p.TelegramUserId == telegramUserId).ToListAsync();
            foreach (var p in pendings)
            {
                if (await VerifyCodeAsync(telegramUserId, p.DeviceId, code)) return true;
            }
            return false;
        }
    }
}
