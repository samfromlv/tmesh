using Microsoft.EntityFrameworkCore;
using TBot.Analytics.Models;
using TBot.Database.Models;

namespace TBot.Analytics;

public class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    public DbSet<DeviceMetric> DeviceMetrics => Set<DeviceMetric>();
    public DbSet<LinkTrace> Traces => Set<LinkTrace>();
    public DbSet<Packet> Packets => Set<Packet>();
    public DbSet<NodeInfo> NodeInfos => Set<NodeInfo>();
    public DbSet<PacketBody> RawPackets => Set<PacketBody>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceMetric>(e =>
        {
            e.HasKey(r => new { r.DeviceId, r.Timestamp });
            e.Property(r => r.DeviceId).IsRequired();
            e.Property(r => r.NetworkId).IsRequired().HasDefaultValue(1);
            e.Property(r => r.Timestamp).IsRequired();
            e.Property(r => r.Latitude);
            e.Property(r => r.Longitude);
            e.Property(r => r.AccuracyMeters);
            e.Property(r => r.LocationUpdatedUtc);
            e.Property(r => r.ChannelUtil);
            e.Property(r => r.AirUtil);

            e.HasIndex(r => r.Timestamp);
            e.HasIndex(r => new { 
                r.NetworkId,
                r.Timestamp,
            });
        });

        modelBuilder.Entity<LinkTrace>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id)
                .ValueGeneratedOnAdd();
            e.Property(r => r.NetworkId).IsRequired().HasDefaultValue(1);
            e.Property(r => r.PacketId).IsRequired();
            e.Property(r => r.FromGatewayId).IsRequired();
            e.Property(r => r.ToGatewayId).IsRequired();
            e.Property(r => r.Step);
            e.Property(r => r.Timestamp).IsRequired();
            e.Property(r => r.RecDate).IsRequired();
            e.Property(r => r.ToLatitude).IsRequired();
            e.Property(r => r.ToLongitude).IsRequired();
            e.Property(r => r.FromLatitude).IsRequired();
            e.Property(r => r.FromLongitude).IsRequired();
            e.HasIndex(r => new { r.NetworkId, r.RecDate })
                .IncludeProperties(r => new
                {
                    r.PacketId,
                    r.FromGatewayId,
                    r.ToGatewayId,
                    r.Step,
                    r.ToLatitude,
                    r.ToLongitude,
                    r.FromLatitude,
                    r.FromLongitude,
                    r.Timestamp
                });
        });

        modelBuilder.Entity<Packet>(e =>
        {
            e.HasKey(r => r.RecordId);
            e.Property(r => r.RecordId).ValueGeneratedOnAdd();
            e.HasIndex(r => r.PacketId);
        });

        modelBuilder.Entity<PacketBody>(e =>
        {
            e.HasKey(r => r.RecordId);
            e.Property(r => r.RecordId).ValueGeneratedNever();
            e.Property(r => r.Body).IsRequired();

        });

        modelBuilder.Entity<NodeInfo>(e =>
        {
            e.HasKey(r => r.RecordId);
            e.Property(r => r.RecordId).ValueGeneratedNever();
        });
    }
}




