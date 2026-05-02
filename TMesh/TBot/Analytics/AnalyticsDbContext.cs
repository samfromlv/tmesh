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
    public DbSet<VoteParticipant> VoteParticipants => Set<VoteParticipant>();
    public DbSet<VoteLog> VoteLogs => Set<VoteLog>();
    public DbSet<VoteSnapshot> VoteSnapshots => Set<VoteSnapshot>();
    public DbSet<VoteSnapshotStats> VoteStats => Set<VoteSnapshotStats>();
    public DbSet<VoteSnapshotRecord> VoteSnapshotRecords => Set<VoteSnapshotRecord>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<VoteOption> VoteOptions => Set<VoteOption>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Vote>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).IsRequired().ValueGeneratedOnAdd();
            e.Property(r => r.NetworkId).IsRequired();
            e.Property(r => r.Name).IsRequired().HasMaxLength(150);
            e.Property(r => r.Description).IsRequired().HasMaxLength(500);
            e.Property(r => r.StartsAt).IsRequired();
            e.Property(r => r.EndsAt).IsRequired();
            e.Property(r => r.Enabled).IsRequired();
            e.Property(r => r.IsActive).IsRequired();
            e.Property(r => r.LastUpdate);
            e.HasMany(r => r.Options)
                .WithOne(o => o.Vote)
                .HasForeignKey(o => o.VoteId)
                .HasPrincipalKey(r => r.Id);

            e.HasIndex(r => new { r.IsActive });
        });

        modelBuilder.Entity<VoteOption>(e =>
        {
            e.HasKey(r => new { r.VoteId, r.OptionId });
            e.Property(r => r.VoteId).IsRequired();
            e.Property(r => r.OptionId).IsRequired();
            e.Property(r => r.Name).IsRequired().HasMaxLength(150);
            e.Property(r => r.Prefix).IsRequired().HasMaxLength(MeshtasticService.MaxLongNodeNameLengthChars / 2);

            e.HasIndex(r => new { r.VoteId, r.Prefix }).IsUnique();
        });

       
        modelBuilder.Entity<VoteLog>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).IsRequired().ValueGeneratedOnAdd();
            e.Property(r => r.DeviceId).IsRequired();
            e.Property(r => r.VoteId).IsRequired();
            e.Property(r => r.ChangeMade).IsRequired();
            e.Property(r => r.LogCreated).IsRequired();
            e.Property(r => r.NewLongName).HasMaxLength(MeshtasticService.MaxLongNodeNameLengthChars);
            e.Property(r => r.MeshPacketId);
            e.Property(r => r.OldOptionId).IsRequired();
            e.Property(r => r.NewOptionId).IsRequired();
            e.Property(r => r.SnapshotId).IsRequired();
            e.Property(r => r.Reason).IsRequired();

            e.HasIndex(r => new { r.VoteId, r.DeviceId, r.ChangeMade });
            e.HasIndex(r => r.SnapshotId);
        });

        modelBuilder.Entity<VoteParticipant>(e =>
        {
            e.HasKey(r => new { r.VoteId, r.DeviceId });
            e.Property(r => r.VoteId).IsRequired();
            e.Property(r => r.DeviceId).IsRequired();
            e.Property(r => r.LongName).IsRequired().HasMaxLength(MeshtasticService.MaxLongNodeNameLengthChars);
            e.Property(r => r.FirstVote).IsRequired();
            e.Property(r => r.LastVote).IsRequired();
            e.Property(r => r.LastVoteChange).IsRequired();
            e.Property(r => r.NodeRegistered).IsRequired();
            e.Property(r => r.CurrentOptionId).IsRequired();
            e.Property(r => r.IsNoVote).IsRequired();
            e.Property(r => r.VoteCount).IsRequired();
        });

        modelBuilder.Entity<VoteSnapshot>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).IsRequired().ValueGeneratedOnAdd();

            e.Property(r => r.VoteId).IsRequired();
            e.Property(r => r.Timestamp).IsRequired();

            e.HasIndex(r => new { r.VoteId, r.Timestamp });
        });

        modelBuilder.Entity<VoteSnapshotRecord>(e =>
        {
            e.HasKey(r => new { r.SnapshotId, r.DeviceId });
            e.Property(r => r.SnapshotId).IsRequired();
            e.Property(r => r.DeviceId).IsRequired();
            e.Property(r => r.LongName).IsRequired().HasMaxLength(MeshtasticService.MaxLongNodeNameLengthChars);
            e.Property(r => r.OptionId).IsRequired();
        });

        modelBuilder.Entity<VoteSnapshotStats>(e =>
        {
            e.HasKey(r => new { r.SnapshotId, r.OptionId });
            e.Property(r => r.SnapshotId).IsRequired();
            e.Property(r => r.OptionId).IsRequired();
            e.Property(r => r.DeltaFromLastSnapshot).IsRequired();
            e.Property(r => r.ActiveCount).IsRequired();
        });



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
            e.HasIndex(r => new
            {
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




