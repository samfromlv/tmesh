using Microsoft.EntityFrameworkCore;
using TBot.Database.Models;

namespace TBot.Database;

public class TBotDbContext(DbContextOptions<TBotDbContext> options) : DbContext(options)
{
    public DbSet<DeviceRegistration> Registrations => Set<DeviceRegistration>();
    public DbSet<Device> Devices => Set<Device>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceRegistration>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id)
                .ValueGeneratedOnAdd();

            e.HasIndex(r => new { r.TelegramUserId, r.ChatId, r.DeviceId })
                .IsUnique();

            e.HasIndex(r => r.TelegramUserId);
            e.HasIndex(r => r.ChatId);
            e.HasIndex(r => r.DeviceId);
            e.Property(r => r.CreatedUtc).IsRequired();
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
        });
    }
}




