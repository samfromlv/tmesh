using Meshtastic.Protobufs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TBot.Models;

namespace TBot;

public class MessageLoopService : IHostedService
{
    private readonly ILogger<MessageLoopService> _logger;
    private readonly TBotOptions _options;
    private readonly IServiceProvider _services;
    private readonly MqttService _mqttService;
    private readonly MeshtasticService _meshtasticService;
    private readonly RegistrationService _registrationService;
    private readonly LocalMessageQueueService _localMessageQueueService;
    private System.Timers.Timer _virtualNodeInfoTimer;

    public MessageLoopService(
        ILogger<MessageLoopService> logger,
        LocalMessageQueueService localMessageQueueService,
        MqttService mqttService,
        MeshtasticService meshtasticService,
        RegistrationService registrationService,
        IOptions<TBotOptions> options,
        IServiceProvider services)
    {
        _logger = logger;
        _options = options.Value;
        _services = services;
        _mqttService = mqttService;
        _meshtasticService = meshtasticService;
        _registrationService = registrationService;
        _localMessageQueueService = localMessageQueueService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _mqttService.TelegramMessageReceivedAsync += HandleTelegramMessage;
        _mqttService.MeshtasticMessageReceivedAsync += HandleMeshtasticMessage;
        await _mqttService.EnsureMqttConnectedAsync(cancellationToken);
        _localMessageQueueService.Start();
        StartVirtualNodeInfoTimer();
        _meshtasticService.SendVirtualNodeInfoAsync();
    }

    private void StartVirtualNodeInfoTimer()
    {
        _virtualNodeInfoTimer = new System.Timers.Timer(_options.SentTBotNodeInfoEverySeconds * 1000);
        _virtualNodeInfoTimer.Elapsed += async (sender, e) =>
        {
            try
            {
                _meshtasticService.SendVirtualNodeInfoAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending virtual node info");
            }
        };
        _virtualNodeInfoTimer.AutoReset = true;
        _virtualNodeInfoTimer.Enabled = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _virtualNodeInfoTimer.Enabled = false;
        await _localMessageQueueService.Stop();
        _mqttService.TelegramMessageReceivedAsync -= HandleTelegramMessage;
        _mqttService.MeshtasticMessageReceivedAsync -= HandleMeshtasticMessage;
        await _mqttService.DisposeAsync();
    }


    private async Task HandleMeshtasticMessage(DataEventArgs<ServiceEnvelope> msg)
    {
        try
        {
            var (isPki, senderDeviceId, receiverDeviceId) = _meshtasticService.GetMessageSenderDeviceId(msg.Data);

            byte[] senderPublicKey = null;
            if (isPki && receiverDeviceId == _options.MeshtasticNodeId)
            {
                var device = await _registrationService.GetDeviceAsync(senderDeviceId);
                senderPublicKey = device?.PublicKey;
            }


            var res = _meshtasticService.TryDecryptMessage(msg.Data, senderPublicKey);

            if (!res.success)
                return;

            using var scope = _services.CreateScope();
            var botService = scope.ServiceProvider.GetRequiredService<BotService>();
            await botService.ProcessInboundMeshtasticMessage(res.msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling MQTT message");
        }
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
