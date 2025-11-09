using Microsoft.EntityFrameworkCore;
using TBot.Database.Models;

namespace TBot.Database;

public class TBotDbContext : DbContext
{
    public TBotDbContext(DbContextOptions<TBotDbContext> options) : base(options) {}
    public DbSet<DeviceRegistration> Registrations => Set<DeviceRegistration>();
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
    }
}




