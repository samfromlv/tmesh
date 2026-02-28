using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Analytics.Models;
using TBot.Helpers;

namespace TBot.Analytics
{
    public class AnalyticsService(AnalyticsDbContext db, TimeZoneHelper tz)
    {
        public async Task RecordEventAsync(DeviceMetric metrics)
        {
            db.DeviceMetrics.Add(metrics);
            await db.SaveChangesAsync();
        }

        public async Task<int> GetStatistics(DateTime fromUtc)
        {
            var from = Instant.FromDateTimeUtc(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc));
            return await db.DeviceMetrics
                .Where(m => m.Timestamp >= from)
                 .CountAsync();
        }

        public async Task RecordLinkTrace(
            long packetId,
            long fromGatewayId,
            long toGatewayId,
            byte? step,
            double toLatitude,
            double toLongitude,
            double fromLatitude,
            double fromLongitude)
        {
            var now = DateTime.UtcNow;
            var localNow = tz.ConvertFromUtcToDefaultTimezone(now);
            db.Traces.Add(new LinkTrace
            {
                PacketId = (uint)packetId,
                FromGatewayId = (uint)fromGatewayId,
                ToGatewayId = (uint)toGatewayId,
                Timestamp = Instant.FromDateTimeUtc(now),
                RecDate = LocalDate.FromDateTime(localNow),
                ToLatitude = toLatitude,
                ToLongitude = toLongitude,
                FromLatitude = fromLatitude,
                FromLongitude = fromLongitude,
                Step = step
            });
            await db.SaveChangesAsync();
        }

        public async Task EnsureMigratedAsync()
        {
            await db.Database.MigrateAsync();
        }
    }
}
