using Microsoft.EntityFrameworkCore;
using TBot.Analytics.Models;
using TBot.Database.Models;

namespace TBot.Analytics;

public class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    public DbSet<DeviceMetric> DeviceMetrics => Set<DeviceMetric>();
    public DbSet<LinkTrace> Traces => Set<LinkTrace>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceMetric>(e =>
        {
            e.HasKey(r => new { r.DeviceId, r.Timestamp });
            e.Property(r => r.DeviceId).IsRequired();
            e.Property(r => r.Timestamp).IsRequired();
            e.Property(r => r.Latitude);
            e.Property(r => r.Longitude);
            e.Property(r => r.AccuracyMeters);
            e.Property(r => r.LocationUpdatedUtc);
            e.Property(r => r.ChannelUtil);
            e.Property(r => r.AirUtil);

            e.HasIndex(r => r.Timestamp);
        });

        modelBuilder.Entity<LinkTrace>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id)
                .ValueGeneratedOnAdd();
            e.Property(r => r.PacketId).IsRequired();
            e.Property(r => r.FromGatewayId).IsRequired();
            e.Property(r => r.ToGatewayId).IsRequired();
            e.Property(r => r.Step);
            e.Property(r => r.Timestamp).IsRequired();
            e.Property(r => r.RecDate).IsRequired();
            e.Property(r => r.ToLatitude).IsRequired();
            e.Property(r => r.ToLongitude).IsRequired();

            e.HasIndex(r => r.RecDate)
                .IncludeProperties(r => new
                {
                    r.PacketId,
                    r.FromGatewayId,
                    r.ToGatewayId,
                    r.Step
                });
        });
    }
}




