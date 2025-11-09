using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

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
                    services.AddHostedService<StartupService>();
                    BotService.Register(services);
                })
                .ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Information);
                });

            using var host = hostBuilder.Build();

            // Handle /install command
            if (args.Any(a => string.Equals(a, "/install", StringComparison.OrdinalIgnoreCase)))
            {
                await HandleInstall(host);
            }
            else if (args.Any(a => string.Equals(a, "/checkinstall", StringComparison.OrdinalIgnoreCase)))
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

            await host.RunAsync();
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
                await botService.Install();
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

    internal class StartupService : IHostedService
    {
        private readonly ILogger<StartupService> _logger;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly TBotOptions _options;

        public StartupService(ILogger<StartupService> logger, IHostApplicationLifetime lifetime, Microsoft.Extensions.Options.IOptions<TBotOptions> options)
        {
            _logger = logger;
            _lifetime = lifetime;
            _options = options.Value;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("TBot starting. MQTT: {Host}:{Port}, Telegram bot token configured: {HasToken}", _options.MqttAddress, _options.MqttPort, !string.IsNullOrEmpty(_options.TelegramApiToken));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("TBot stopping.");
            return Task.CompletedTask;
        }
    }
}
