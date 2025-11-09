using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TBot
{
    public class BotService
    {
        public BotService(TelegramBotClient botClient, IOptions<TBotOptions> options)
        {
            _botClient = botClient;
            _options = options.Value;
        }
        private readonly TelegramBotClient _botClient;  
        private readonly TBotOptions _options;

        public async Task Install()
        {
            await _botClient.SetWebhook(_options.TelegramUpdateWebhookUrl,
                certificate: null,
                ipAddress: null,
                maxConnections: _options.TelegramBotMaxConnections,
                allowedUpdates: [UpdateType.Message],
                secretToken: _options.TelegramWebhookSecret);
        }

        public async Task<WebhookInfo> CheckInstall()
        {
            return await _botClient.GetWebhookInfo();
        }

        public static void Register(IServiceCollection services)
        {
            services.AddHttpClient("tgapi");
            services.AddScoped(s =>
            {
                var options = s.GetRequiredService<IOptions<TBotOptions>>();
                return new TelegramBotClient(options.Value.TelegramApiToken,
                    s.GetRequiredService<IHttpClientFactory>().CreateClient("tgapi"));
            });
            services.AddScoped<BotService>();
        }
    }
}
