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
                    // Prefer external config volume /tbot/config or env TBOT_CONFIG_PATH
                    var configPath = Environment.GetEnvironmentVariable("TBOT_CONFIG_PATH")?.Trim();
                    if (string.IsNullOrWhiteSpace(configPath)) configPath = "/tbot/config"; // default inside container
                    var filePath = Path.Combine(configPath, "appsettings.json");

                    // If running locally (not container) allow fallback to local appsettings.json
                    var inContainer = Directory.Exists("/tbot");
                    if (inContainer)
                    {
                        config.AddJsonFile(filePath, optional: false, reloadOnChange: true);
                    }
                    else
                    {
                        // local dev
                        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                    }
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

            // Handle /install command
            if (args.Any(a => string.Equals(a, "/installwebhook", StringComparison.OrdinalIgnoreCase)))
            {
                await InstallWebHook(host);
                return;
            }
            else if (args.Any(a => string.Equals(a, "/checkinstallwebhook", StringComparison.OrdinalIgnoreCase)))
            {
                await CheckInstallWebHook(host);
                return;
            }
            else if (args.Any(a => string.Equals(a, "/updatedb", StringComparison.OrdinalIgnoreCase)))
            {
                await UpdateDb(host);
                return;
            }
            else if (args.Any(a => string.Equals(a, "/generatekeys", StringComparison.OrdinalIgnoreCase)))
            {
                GenerateKeys(host);
                Console.ReadLine();
                return;
            }

            await host.RunAsync();
        }

        private static async Task CheckInstallWebHook(IHost host)
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

        private static async Task InstallWebHook(IHost host)
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

        private static async Task UpdateDb(IHost host)
        {
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            try
            {
                var options = host.Services.GetRequiredService<IOptions<TBotOptions>>().Value;
                if (string.IsNullOrWhiteSpace(options.SQLiteConnectionString))
                {
                    logger.LogError("Missing SQLiteConnectionString in configuration. Aborting /updatedb.");
                    return; // exit non-zero? keep zero for simplicity
                }
                var regService = host.Services.GetRequiredService<RegistrationService>();
                await regService.EnsureMigratedAsync();
                logger.LogInformation("Database update completed successfully.");
            }
            catch (Exception ex)
            {
                var logger2 = host.Services.GetRequiredService<ILogger<Program>>();
                logger2.LogError(ex, "Database update failed.");
            }
            return; // exit after install
        }


        private static void GenerateKeys(IHost host)
        {
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            try
            {
                var service = host.Services.GetRequiredService<MeshtasticService>();
                var pair = service.GenerateKeyPair();
                logger.LogInformation("Generated Key Pair:");
                logger.LogInformation("PublicKey=[{PublicKey}]", pair.publicKeyBase64);
                logger.LogInformation("PrivateKey=[{PrivateKey}]", pair.privateKeyBase64);
            }
            catch (Exception ex)
            {
                var logger2 = host.Services.GetRequiredService<ILogger<Program>>();
                logger2.LogError(ex, "Key pair generation failed.");
            }
            return; // exit after install
        }
    }
}
