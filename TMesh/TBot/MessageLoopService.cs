using Meshtastic.Protobufs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Models;
using System.Collections.Concurrent;
using System.Timers;
using TBot.Analytics;
using TBot.Database.Models;
using TBot.Models;
using TBot.Models.MeshMessages;

namespace TBot;

public class MessageLoopService(
    ILogger<MessageLoopService> logger,
    LocalMessageQueueService localMessageQueueService,
    MqttService mqttService,
    MeshtasticService meshtasticService,
    IOptions<TBotOptions> options,
    IServiceProvider services,
    SimpleScheduler scheduler) : IHostedService
{
    private const int CheckGatewayNodeInfoLAstSeenAfterMinutes = 60;
    private readonly TBotOptions _options = options.Value;
    private System.Timers.Timer _serviceInfoTimer;
    private DateTime _lastVirtualNodeInfoSent = DateTime.MinValue;
    private DateTime _started;
    private readonly ConcurrentDictionary<long, DateTime> _gatewayLastSeen = new();

    private BlockingCollection<AckMessage> _ackQueue;
    private readonly SemaphoreSlim _ackQueueSemaphore = new(0);
    private Task _ackWorker;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _started = DateTime.UtcNow;
        mqttService.TelegramMessageReceivedAsync += HandleTelegramMessage;
        mqttService.MeshtasticMessageReceivedAsync += HandleMeshtasticMessage;
        mqttService.MessageSent += HandleMessageSent;
        await mqttService.EnsureMqttConnectedAsync(cancellationToken);
        localMessageQueueService.Start();
        StartServiceInfoInfoTimer();

#pragma warning disable IDE0028 // Simplify collection initialization
        _ackQueue = new BlockingCollection<AckMessage>();
#pragma warning restore IDE0028 // Simplify collection initialization
        _ackWorker = AckWorker(_ackQueue);
        SendVirtualNodeInfo();
        await PublishStats();
    }

    private void SendVirtualNodeInfo()
    {
        _lastVirtualNodeInfoSent = DateTime.UtcNow;
        meshtasticService.SendVirtualNodeInfo();
    }

    private async Task HandleMessageSent(DataEventArgs<long> args)
    {
        using var scope = services.CreateScope();
        var botService = scope.ServiceProvider.GetRequiredService<BotService>();
        await botService.ProcessMessageSent(args.Data);
    }

    private void StartServiceInfoInfoTimer()
    {
        _serviceInfoTimer = new System.Timers.Timer(60 * 1000);
        _serviceInfoTimer.Elapsed += ServiceInfoTimer_Elapsed;
        _serviceInfoTimer.AutoReset = true;
        _serviceInfoTimer.Enabled = true;
    }

    private async void ServiceInfoTimer_Elapsed(object sender, ElapsedEventArgs e)
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
            logger.LogError(ex, "Error in ServiceInfo timer");
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
            Mesh5Min = meshtasticService.AggregateStartFrom(min5ago),
            Mesh15Min = meshtasticService.AggregateStartFrom(min15ago),
            Mesh1Hour = meshtasticService.AggregateStartFrom(hour1ago),
            LastUpdate = now,
            Started = _started
        };

        using var scope = services.CreateScope();
        var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
        var analyticsService = scope.ServiceProvider.GetService<AnalyticsService>();
        if (analyticsService != null)
        {
            botStats.TelemetrySaved24H = await analyticsService.GetStatistics(now.AddHours(-24));
        }

        botStats.ChatRegistrations = await registrationService.GetTotalRegistrationsCount();
        botStats.Devices = await registrationService.GetTotalDevicesCount();
        botStats.Devices24h = await registrationService.GetActiveDevicesCount(now.AddHours(-24));
        botStats.GatewaysLastSeen = await GetGatewaysLastSeenStat(now, registrationService);
        
        
        await mqttService.PublishStatus(botStats);
    }

    private async ValueTask<Dictionary<string, DateTime?>> GetGatewaysLastSeenStat(DateTime utcNow, RegistrationService regService)
    {
        var stat = new Dictionary<string, DateTime?>(_options.GatewayNodeIds.Length);
        foreach (var gwId in _options.GatewayNodeIds.OrderBy(x => x))
        {
            if (!_gatewayLastSeen.TryGetValue(gwId, out var lastSeen))
            {
                lastSeen = DateTime.MinValue;
            }
            if ((utcNow - lastSeen).TotalMinutes > CheckGatewayNodeInfoLAstSeenAfterMinutes)
            {
                var gw = await regService.GetDeviceAsync(gwId);
                if (gw != null && gw.UpdatedUtc > lastSeen)
                {
                    lastSeen = gw.UpdatedUtc;
                }
            }
            stat[MeshtasticService.GetMeshtasticNodeHexId(gwId)] = lastSeen == DateTime.MinValue ? null : lastSeen;
        }
        return stat;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        scheduler.Dispose();
        _ackQueue.CompleteAdding();
        _ackQueueSemaphore.Release();
        await _ackWorker;
        _ackQueue.Dispose();
        _ackQueue = null;
        _ackWorker = null;
        _serviceInfoTimer.Enabled = false;
        await localMessageQueueService.Stop();
        mqttService.TelegramMessageReceivedAsync -= HandleTelegramMessage;
        mqttService.MeshtasticMessageReceivedAsync -= HandleMeshtasticMessage;
        mqttService.MessageSent -= HandleMessageSent;
        await mqttService.DisposeAsync();
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
                            using var scope = services.CreateScope();
                            var botService = scope.ServiceProvider.GetRequiredService<BotService>();
                            await botService.ProcessAckMessages(batch);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error in AckWorker");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in AckWorker outer");
            }
        });
    }


    private async Task HandleMeshtasticMessage(DataEventArgs<ServiceEnvelope> msg)
    {
        IServiceScope scope = null;
        try
        {
            if (msg.Data == null)
            {
                logger.LogWarning("Received Meshtastic message with null data");
                return;
            }

            if (!MeshtasticService.TryParseDeviceId(msg.Data.GatewayId, out var gatewayId))
            {
                logger.LogWarning("Received Meshtastic message with invalid gateway ID format: {GatewayId}", msg.Data.GatewayId);
                return;
            }

            if (!_options.GatewayNodeIds.Contains(gatewayId))
            {
                logger.LogWarning("Received Meshtastic message from unregistered gateway ID {GatewayId}", gatewayId);
                return;
            }

            UpdateGatewayLastSeen(gatewayId);

            if (meshtasticService.TryBridge(msg.Data))
            {
                return;
            }

            var packetFromTo = MeshtasticService.GetPacketAddresses(msg.Data);
            Device device = null;
            var recipients = new List<IRecipient>(8);
            RegistrationService registrationService = null;
            if (packetFromTo.IsPkiEncrypted && packetFromTo.To == _options.MeshtasticNodeId)
            {
                scope ??= services.CreateScope();
                registrationService ??= scope.ServiceProvider.GetRequiredService<RegistrationService>();
                device = await registrationService.GetDeviceAsync(packetFromTo.From);
                if (device != null)
                {   
                    recipients.Add(device);
                }   
            }
            if (!packetFromTo.IsPkiEncrypted)
            {
                scope ??= services.CreateScope();
                registrationService ??= scope.ServiceProvider.GetRequiredService<RegistrationService>();
                recipients.AddRange(meshtasticService.GetPublicChannelsByHash(packetFromTo.XorHash));
                var channelRecipients = await registrationService.GetChannelKeysByHashCached(packetFromTo.XorHash);
                recipients.AddRange(channelRecipients);
            }

            var res = meshtasticService.TryDecryptMessage(msg.Data, recipients);
            if (!res.success)
            {
                return;
            }

            if (res.msg.MessageType == MeshMessageType.AckMessage)
            {
                var ackMessage = (AckMessage)res.msg;
                EnqueueAckMessage(ackMessage);
                return;
            }

            scope ??= services.CreateScope();

            var botService = scope.ServiceProvider.GetRequiredService<BotService>();
            await botService.ProcessInboundMeshtasticMessage(res.msg, device);
            ScheduleStatusResolve(botService.TrackedMessages);

        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling MQTT message");
        }
        finally
        {
            scope?.Dispose();
        }
    }

    private void UpdateGatewayLastSeen(long gatewayId)
    {
        var now = DateTime.UtcNow;
        _gatewayLastSeen.AddOrUpdate(gatewayId, now, (key, oldValue) => oldValue > now ? oldValue : now);
    }

    private async Task HandleTelegramMessage(DataEventArgs<string> msg)
    {
        try
        {
            using var scope = services.CreateScope();
            var botService = scope.ServiceProvider.GetRequiredService<BotService>();
            await botService.ProcessInboundTelegramMessage(msg.Data);
            ScheduleStatusResolve(botService.TrackedMessages);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling MQTT message");
        }
    }

    private void ScheduleStatusResolve(IEnumerable<MeshtasticMessageStatus> msgStatuses)
    {
        foreach (var msgStatus in msgStatuses)
        {
            var delay = (msgStatus.EstimatedSendDate ?? DateTime.UtcNow)
                .AddMinutes(MeshtasticService.WaitForAckStatusMaxMinutes)
                .Subtract(DateTime.UtcNow);

            scheduler.Schedule(delay, () =>
                ProcessResolveStatusOfMeshtasticMessage(
                    msgStatus.TelegramChatId,
                    msgStatus.TelegramMessageId));
        }
    }

    private async Task ProcessResolveStatusOfMeshtasticMessage(long telegramChatId, int telegramMessageId)
    {
        try
        {
            using var scope = services.CreateScope();
            var botService = scope.ServiceProvider.GetRequiredService<BotService>();
            await botService.ResolveMessageStatus(telegramChatId, telegramMessageId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing Meshtastic message status");
        }
    }
}
