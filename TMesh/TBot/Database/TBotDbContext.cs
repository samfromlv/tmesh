using Microsoft.EntityFrameworkCore;
using TBot.Database.Models;

namespace TBot.Database;

public class TBotDbContext(DbContextOptions<TBotDbContext> options) : DbContext(options)
{
    public const int MaxChatNameLength = 32;
    public DbSet<DeviceRegistration> DeviceRegistrations => Set<DeviceRegistration>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<PublicChannel> PublicChannels => Set<PublicChannel>();
    public DbSet<Network> Networks => Set<Network>();
    public DbSet<ChannelRegistration> ChannelRegistrations => Set<ChannelRegistration>();
    public DbSet<GatewayRegistration> GatewayRegistrations => Set<GatewayRegistration>();
    public DbSet<TgChat> TgChats => Set<TgChat>();
    public DbSet<TgChatApprovedDevice> TgChatApprovedDevices => Set<TgChatApprovedDevice>();
    public DbSet<TgChatApprovedChannel> TgChatApprovedChannels => Set<TgChatApprovedChannel>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ScheduledMessage> ScheduledMessages => Set<ScheduledMessage>();
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

            e.Property(r => r.NetworkId).IsRequired()
               .HasDefaultValue(1);

            e.Property(p => p.NodeName);
            e.Property(p => p.HardwareModel);
            e.Property(p => p.MacAddress);
            e.Property(p => p.CreatedUtc)
                .IsRequired();
            e.Property(p => p.UpdatedUtc)
                .IsRequired();

            e.Property(p => p.HasRegistrations);

            e.Property(p => p.Latitude);
            e.Property(p => p.Longitude);
            e.Property(p => p.LocationUpdatedUtc);
            e.Property(p => p.AccuracyMeters);

            e.HasIndex(r => r.NetworkId);

            e.HasIndex(r => new { r.NetworkId, r.UpdatedUtc });

        });

        modelBuilder.Entity<GatewayRegistration>(e =>
        {
            e.HasKey(p => p.DeviceId);
            e.Property(p => p.NetworkId)
                .IsRequired()
                .HasDefaultValue(1);
            e.Property(p => p.CreatedUtc)
                .IsRequired();
            e.Property(p => p.UpdatedUtc)
                .IsRequired();
            e.Property(p => p.LastSeenUtc);
            e.HasIndex(p => p.NetworkId);
        });

        modelBuilder.Entity<Channel>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(r => r.Id)
               .ValueGeneratedOnAdd();

            e.Property(r => r.NetworkId).IsRequired()
               .HasDefaultValue(1);

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

            e.HasIndex(p => new { p.NetworkId, p.Name, p.Key })
                .IsUnique();

            e.HasIndex(p => new { p.NetworkId, p.XorHash });
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

        modelBuilder.Entity<Network>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(r => r.Id);
            e.Property(p => p.Name)
                .IsRequired();
            e.Property(p => p.Url);
            e.Property(p => p.CommunityUrl);
            e.Property(p => p.WelcomeUrl);
            e.Property(p => p.DisablePongs)
                .IsRequired()
                .HasDefaultValue(false);

            e.Property(p => p.DisableWelcomeMessage)
                .IsRequired()
                .HasDefaultValue(false);

            e.Property(p => p.SortOrder)
                .IsRequired()
                .HasDefaultValue(0);

            e.Property(p => p.SaveAnalytics);

            e.Property(p => p.ShortName)
                .IsRequired();

            e.HasIndex(p => p.Name)
                .IsUnique();
        });

        modelBuilder.Entity<PublicChannel>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(r => r.Id)
               .ValueGeneratedOnAdd();
            e.Property(r => r.NetworkId).IsRequired();
            e.Property(p => p.Name)
                .IsRequired()
                // Use binary collation for the Name property to ensure case-sensitive and accent-sensitive comparisons
                .UseCollation("BINARY");

            e.Property(p => p.Key)
                .IsRequired();

            e.Property(p => p.XorHash)
                .IsRequired();

            e.Property(p => p.IsPrimary)
                .IsRequired()
                .HasDefaultValue(false);

            e.Property(p => p.SendNodeInfoOnSecondary)
                .IsRequired()
                .HasDefaultValue(false);

            e.Property(p => p.CreatedUtc)
                .IsRequired();

            e.HasIndex(p => p.NetworkId);
            e.HasIndex(p => p.IsPrimary);
        }
        );

        modelBuilder.Entity<TgChat>(e =>
        {
            e.HasKey(p => p.ChatId);
            e.Property(p => p.ChatId).IsRequired();
            e.Property(p => p.IsPrivate).IsRequired();
            e.Property(p => p.ChatName)
                .IsRequired()
                .HasMaxLength(MaxChatNameLength);
            e.Property(p => p.ChatKey)
                .IsRequired()
                .HasMaxLength(MaxChatNameLength);
            e.Property(p => p.IsActive).IsRequired();
            e.Property(p => p.CreatedUtc).IsRequired();
            e.HasIndex(p => p.ChatKey).IsUnique();
        });

        modelBuilder.Entity<TgChatApprovedDevice>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).ValueGeneratedOnAdd();
            e.Property(p => p.TgChatId).IsRequired();
            e.Property(p => p.DeviceId).IsRequired();
            e.Property(p => p.CreatedUtc).IsRequired();

            e.HasIndex(p => new { p.TgChatId, p.DeviceId }).IsUnique();
            e.HasIndex(p => p.DeviceId);
        });

        modelBuilder.Entity<TgChatApprovedChannel>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).ValueGeneratedOnAdd();
            e.Property(p => p.TgChatId).IsRequired();
            e.Property(p => p.ChannelId).IsRequired();
            e.Property(p => p.CreatedUtc).IsRequired();
            e.HasIndex(p => new { p.TgChatId, p.ChannelId }).IsUnique();
            e.HasIndex(p => p.ChannelId);

        });

        modelBuilder.Entity<ChatSession>(e =>
        {
            e.HasKey(p => p.ChatId);
            e.Property(p => p.ChatId).IsRequired();
            e.Property(p => p.DeviceId);
            e.Property(p => p.ChannelId);
            e.Property(p => p.ExpirationDate).IsRequired();
            e.HasIndex(p => p.ExpirationDate);
        });

        modelBuilder.Entity<ScheduledMessage>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).ValueGeneratedOnAdd();
            e.Property(p => p.PublicChannelId).IsRequired();
            e.Property(p => p.Text).IsRequired();
            e.Property(p => p.IntervalMinutes).IsRequired();
            e.Property(p => p.LastSentUtc);
            e.Property(p => p.Enabled).IsRequired().HasDefaultValue(true);
            e.Property(p => p.EnableAt);
            e.Property(p => p.DisableAt);
            e.HasIndex(p => p.PublicChannelId);
            e.HasIndex(p => p.Enabled);
        });
    }
}




