using Meshtastic.Protobufs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Models;
using System.Collections.Concurrent;
using System.Timers;
using TBot.Database.Models;
using TBot.Models;

namespace TBot;

public class MessageLoopService : IHostedService
{
    private readonly ILogger<MessageLoopService> _logger;
    private readonly TBotOptions _options;
    private readonly IServiceProvider _services;
    private readonly MqttService _mqttService;
    private readonly MeshtasticService _meshtasticService;
    private readonly LocalMessageQueueService _localMessageQueueService;
    private System.Timers.Timer _serviceInfoTimer;
    private DateTime _lastVirtualNodeInfoSent = DateTime.MinValue;
    private DateTime _started;

    private BlockingCollection<AckMessage> _ackQueue;
    private SemaphoreSlim _ackQueueSemaphore = new SemaphoreSlim(0);
    private Task _ackWorker;

    public MessageLoopService(
        ILogger<MessageLoopService> logger,
        LocalMessageQueueService localMessageQueueService,
        MqttService mqttService,
        MeshtasticService meshtasticService,
        IOptions<TBotOptions> options,
        IServiceProvider services)
    {
        _logger = logger;
        _options = options.Value;
        _services = services;
        _mqttService = mqttService;
        _meshtasticService = meshtasticService;
        _localMessageQueueService = localMessageQueueService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _started = DateTime.UtcNow;
        _mqttService.TelegramMessageReceivedAsync += HandleTelegramMessage;
        _mqttService.MeshtasticMessageReceivedAsync += HandleMeshtasticMessage;
        _mqttService.MessageSent += HandleMessageSent;
        await _mqttService.EnsureMqttConnectedAsync(cancellationToken);
        _localMessageQueueService.Start();
        StartServiceInfoInfoTimer();

        _ackQueue = new BlockingCollection<AckMessage>();
        _ackWorker = AckWorker(_ackQueue);
        SendVirtualNodeInfo();
        await PublishStats();
    }

    private void SendVirtualNodeInfo()
    {
        _lastVirtualNodeInfoSent = DateTime.UtcNow;
        _meshtasticService.SendVirtualNodeInfo();
    }

    private async Task HandleMessageSent(DataEventArgs<long> args)
    {
        using var scope = _services.CreateScope();
        var botService = scope.ServiceProvider.GetRequiredService<BotService>();
        await botService.ProcessMessageSent(args.Data);
    }

    private void StartServiceInfoInfoTimer()
    {
        _serviceInfoTimer = new System.Timers.Timer(60 * 1000);
        _serviceInfoTimer.Elapsed += _serviceInfoTimer_Elapsed;
        _serviceInfoTimer.AutoReset = true;
        _serviceInfoTimer.Enabled = true;
    }

    private async void _serviceInfoTimer_Elapsed(object sender, ElapsedEventArgs e)
    {
        try
        {
            if ((DateTime.UtcNow - _lastVirtualNodeInfoSent).TotalSeconds >= _options.SentTBotNodeInfoEverySeconds)
            {
                SendVirtualNodeInfo();
            }

            await PublishStats();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ServiceInfo timer");
        }
    }

    private async Task PublishStats()
    {
        var now = DateTime.UtcNow;
        var min5ago = now.AddMinutes(-5);
        var min15ago = now.AddMinutes(-15);
        var hour1ago = now.AddHours(-1);

        var botStats = new BotStats
        {
            Mesh5Min = _meshtasticService.AggregateStartFrom(min5ago),
            Mesh15Min = _meshtasticService.AggregateStartFrom(min15ago),
            Mesh1Hour = _meshtasticService.AggregateStartFrom(hour1ago),
            LastUpdate = now,
            Started = _started
        };

        using var scope = _services.CreateScope();
        var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();

        botStats.ChatRegistrations = await registrationService.GetTotalRegistrationsCount();
        botStats.Devices = await registrationService.GetTotalDevicesCount();

        await _mqttService.PublishStatus(botStats);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ackQueue.CompleteAdding();
        _ackQueueSemaphore.Release();
        await _ackWorker;
        _ackQueue.Dispose();
        _ackQueue = null;
        _ackWorker = null;
        _serviceInfoTimer.Enabled = false;
        await _localMessageQueueService.Stop();
        _mqttService.TelegramMessageReceivedAsync -= HandleTelegramMessage;
        _mqttService.MeshtasticMessageReceivedAsync -= HandleMeshtasticMessage;
        _mqttService.MessageSent -= HandleMessageSent;
        await _mqttService.DisposeAsync();
    }

    private void EnqueueAckMessage(AckMessage ackMessage)
    {
        if (_ackQueue == null || _ackQueue.IsAddingCompleted)
            return;

        _ackQueue.Add(ackMessage);
        _ackQueueSemaphore.Release();
    }

    public Task AckWorker(BlockingCollection<AckMessage> ackQueue)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (ackQueue != null && !ackQueue.IsCompleted)
                {
                    try
                    {
                        await _ackQueueSemaphore.WaitAsync();
                        var batch = new List<AckMessage>();
                        while (ackQueue.TryTake(out var ackMessage))
                        {
                            batch.Add(ackMessage);
                        }
                        if (batch.Count > 0)
                        {
                            using var scope = _services.CreateScope();
                            var botService = scope.ServiceProvider.GetRequiredService<BotService>();
                            await botService.ProcessAckMessages(batch);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in AckWorker");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in AckWorker outer");
            }
        });
    }


    private async Task HandleMeshtasticMessage(DataEventArgs<ServiceEnvelope> msg)
    {
        IServiceScope scope = null;
        try
        {
            var (isPki, senderDeviceId, receiverDeviceId) = _meshtasticService.GetMessageSenderDeviceId(msg.Data);

            Device device = null;
            if (isPki && receiverDeviceId == _options.MeshtasticNodeId)
            {
                scope = _services.CreateScope();
                var _registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
                device = await _registrationService.GetDeviceAsync(senderDeviceId);
            }

            var res = _meshtasticService.TryDecryptMessage(msg.Data, device?.PublicKey);

            if (!res.success)
                return;

            if (res.msg.MessageType == MeshMessageType.AckMessage)
            {
                var ackMessage = (AckMessage)res.msg;
                EnqueueAckMessage(ackMessage);
                return;
            }

            scope ??= _services.CreateScope();

            var botService = scope.ServiceProvider.GetRequiredService<BotService>();
            await botService.ProcessInboundMeshtasticMessage(res.msg, device);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling MQTT message");
        }
        finally
        {
            scope?.Dispose();
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
