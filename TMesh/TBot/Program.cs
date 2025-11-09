using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using TBot.Database;

namespace TBot
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var hostBuilder = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration(config =>
                {
                    config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                    config.AddEnvironmentVariables();
                })
                .ConfigureServices((ctx, services) =>
                {
                    services.Configure<TBotOptions>(ctx.Configuration.GetSection("TBot"));
                    services.AddDbContext<TBotDbContext>((s, opt) =>
                    {
                        var options = s.GetRequiredService<IOptions<TBotOptions>>();
                        opt.UseSqlite(options.Value.SQLiteConnectionString);
                    });
                    services.AddMemoryCache();
                    services.AddSingleton<MqttService>();
                    services.AddHostedService<MessageLoopService>();
                    BotService.Register(services);
                })
                .ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Information);
                });

            using var host = hostBuilder.Build();

            // Apply migrations
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TBotDbContext>();
                await db.Database.MigrateAsync();
            }

            // Handle /install command
            if (args.Any(a => string.Equals(a, "/install", StringComparison.OrdinalIgnoreCase)))
            {
                await HandleInstall(host);
                return;
            }
            else if (args.Any(a => string.Equals(a, "/checkinstall", StringComparison.OrdinalIgnoreCase)))
            {
                await HandleCheckInstall(host);
                return;
            }

            await host.RunAsync();
        }

        private static async Task HandleCheckInstall(IHost host)
        {
            var botService = host.Services.GetRequiredService<BotService>();
            var info = await botService.CheckInstall();
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("Webhook Info: Url={Url}, HasCustomCertificate={HasCustomCertificate}, PendingUpdateCount={PendingUpdateCount}, LastErrorDate={LastErrorDate}, LastErrorMessage={LastErrorMessage}",
                info.Url,
                info.HasCustomCertificate,
                info.PendingUpdateCount,
                info.LastErrorDate,
                info.LastErrorMessage);
        }

        private static async Task HandleInstall(IHost host)
        {
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            try
            {
                var options = host.Services.GetRequiredService<IOptions<TBotOptions>>().Value;
                if (string.IsNullOrWhiteSpace(options.TelegramApiToken) || string.IsNullOrWhiteSpace(options.TelegramUpdateWebhookUrl))
                {
                    logger.LogError("Missing TelegramApiToken or TelegramUpdateWebhookUrl in configuration. Aborting /install.");
                    return; // exit non-zero? keep zero for simplicity
                }
                var botService = host.Services.GetRequiredService<BotService>();
                logger.LogInformation("Installing webhook {WebhookUrl}", options.TelegramUpdateWebhookUrl);
                await botService.InstallWebhook();
                logger.LogInformation("Webhook installation completed successfully.");
            }
            catch (Exception ex)
            {
                var logger2 = host.Services.GetRequiredService<ILogger<Program>>();
                logger2.LogError(ex, "Webhook installation failed.");
            }
            return; // exit after install
        }
    }
}
