using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Analytics.Models;

namespace TBot.Analytics
{
    public class AnalyticsService(AnalyticsDbContext db)
    {
        public async Task RecordEventAsync(DeviceMetric metrics)
        {
            db.DeviceMetrics.Add(metrics);
            await db.SaveChangesAsync();
        }

        public async Task EnsureMigratedAsync()
        {
            await db.Database.MigrateAsync();
        }
    }
}
