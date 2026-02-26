using Microsoft.EntityFrameworkCore;
using TBot.Database.Models;

namespace TBot.Database;

public class TBotDbContext(DbContextOptions<TBotDbContext> options) : DbContext(options)
{
    public DbSet<DeviceRegistration> DeviceRegistrations => Set<DeviceRegistration>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelRegistration> ChannelRegistrations => Set<ChannelRegistration>();
    public DbSet<GatewayRegistration> GatewayRegistrations => Set<GatewayRegistration>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceRegistration>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id)
                .ValueGeneratedOnAdd();
            e.Property(r => r.TelegramUserId).IsRequired();
            e.Property(r => r.ChatId).IsRequired();
            e.Property(r => r.DeviceId).IsRequired();
            e.Property(r => r.CreatedUtc).IsRequired();

            e.HasIndex(r => new { r.TelegramUserId, r.ChatId, r.DeviceId })
                .IsUnique();

            e.HasIndex(r => r.TelegramUserId);
            e.HasIndex(r => r.ChatId);
            e.HasIndex(r => r.DeviceId);
        });

        modelBuilder.Entity<Device>(e =>
        {
            e.HasKey(p => p.DeviceId);
            e.Property(p => p.PublicKey)
                .IsRequired();
            e.Property(p => p.NodeName);
            e.Property(p => p.CreatedUtc)
                .IsRequired();
            e.Property(p => p.UpdatedUtc)
                .IsRequired();

            e.Property(p => p.HasRegistrations);

            e.Property(p => p.Latitude);
            e.Property(p => p.Longitude);
            e.Property(p => p.LocationUpdatedUtc);
            e.Property(p => p.AccuracyMeters);
        });

        modelBuilder.Entity<GatewayRegistration>(e =>
        {
            e.HasKey(p => p.DeviceId);
            e.Property(p => p.CreatedUtc)
                .IsRequired();
            e.Property(p => p.UpdatedUtc)
                .IsRequired();
        });

        modelBuilder.Entity<Channel>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(r => r.Id)
               .ValueGeneratedOnAdd();
            e.Property(p => p.Name)
                .IsRequired()
                // Use binary collation for the Name property to ensure case-sensitive and accent-sensitive comparisons
                .UseCollation("BINARY");

            e.Property(p => p.Key)
                .IsRequired();
            e.Property(p => p.XorHash)
                .IsRequired();

            e.Property(p => p.CreatedUtc)
                .IsRequired();

            e.Property(p => p.IsSingleDevice);

            e.HasIndex(p => new { p.Name, p.Key })
                .IsUnique();

            e.HasIndex(p => p.XorHash);
        });

        modelBuilder.Entity<ChannelRegistration>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id)
                .ValueGeneratedOnAdd();
            e.Property(r => r.TelegramUserId).IsRequired();
            e.Property(r => r.ChatId).IsRequired();
            e.Property(r => r.ChannelId).IsRequired();
            e.Property(r => r.CreatedUtc).IsRequired();

            e.HasIndex(r => r.TelegramUserId);
            e.HasIndex(r => r.ChatId);
            e.HasIndex(r => r.ChannelId);

        });
    }
}




