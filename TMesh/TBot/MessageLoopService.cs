using MQTTnet;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using TBot.Data;
using TBot.Models;

namespace TBot;

public class MessageLoopService : IHostedService
{
    private readonly ILogger<MessageLoopService> _logger;
    private readonly TBotOptions _options;
    private readonly IServiceProvider _services;
    private readonly MqttService _mqttService;

    public MessageLoopService(
        ILogger<MessageLoopService> logger, 
        MqttService mqttService,
        IOptions<TBotOptions> options,
        IServiceProvider services)
    {
        _logger = logger;
        _options = options.Value;
        _services = services;
        _mqttService = mqttService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _mqttService.TelegramMessageReceivedAsync += HandleTelegramMessage;
        await _mqttService.EnsureMqttConnectedAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _mqttService.TelegramMessageReceivedAsync -= HandleTelegramMessage;
        await _mqttService.DisposeAsync();
    }


    private async Task HandleTelegramMessage(DataEventArgs<string> msg)
    {
        try
        {
            using var scope = _services.CreateScope();
            var botService = scope.ServiceProvider.GetRequiredService<BotService>();
            await botService.ProcessInboundTelegramMessage(msg.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling MQTT message");
        }
    }
}
