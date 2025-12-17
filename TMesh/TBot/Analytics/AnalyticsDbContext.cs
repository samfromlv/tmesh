using Microsoft.EntityFrameworkCore;
using TBot.Analytics.Models;
using TBot.Database.Models;

namespace TBot.Analytics;

public class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    public DbSet<DeviceMetric> DeviceMetrics => Set<DeviceMetric>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

        modelBuilder.Entity<DeviceMetric>(e =>
        {
            e.HasKey(r => new { r.DeviceId, r.Timestamp });
            e.Property(r => r.DeviceId);
            e.Property(r => r.Timestamp);
            e.Property(r => r.Latitude);
            e.Property(r => r.Longitude);
            e.Property(r => r.AccuracyMeters);
            e.Property(r => r.LocationUpdatedUtc);
            e.Property(r => r.ChannelUtil);
            e.Property(r => r.AirUtil);
        });
    }
}




