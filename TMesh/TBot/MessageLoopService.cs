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
    private HashSet<long> _gatewayIds;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _started = DateTime.UtcNow;
        await FillGatewayIds();
        mqttService.TelegramMessageReceivedAsync += HandleTelegramMessage;
        mqttService.MeshtasticMessageReceivedAsync += HandleMeshtasticMessage;
        mqttService.MessageSent += HandleMessageSent;
        await mqttService.EnsureMqttConnectedAsync(cancellationToken);
        localMessageQueueService.SendMessage += LocalMessageQueueService_SendMessage;
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

    private Task LocalMessageQueueService_SendMessage(DataEventArgs<QueuedMessage> arg)
    {
        var relayThroughGatewayId = arg.Data.RelayThroughGatewayId;
        if (relayThroughGatewayId.HasValue && !_gatewayIds.Contains(relayThroughGatewayId.Value))
        {
            relayThroughGatewayId = null;
        }

        meshtasticService.StoreNoDup(arg.Data.Message.Packet.Id);

        return mqttService.PublishMeshtasticMessage(
            arg.Data.Message,
            relayThroughGatewayId);
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

        botStats.DeviceChatRegistrations = await registrationService.GetTotalDeviceRegistrationsCount();
        botStats.ChannelChatRegistrations = await registrationService.GetTotalChannelRegistrationsCount();
        botStats.Devices = await registrationService.GetTotalDevicesCount();
        botStats.Devices24h = await registrationService.GetActiveDevicesCount(now.AddHours(-24));
        botStats.GatewaysLastSeen = await GetGatewaysLastSeenStat(now, registrationService);


        await mqttService.PublishStatus(botStats);
    }

    private async ValueTask<Dictionary<string, DateTime?>> GetGatewaysLastSeenStat(DateTime utcNow, RegistrationService regService)
    {
        var stat = new Dictionary<string, DateTime?>(_gatewayIds.Count);
        foreach (var gwId in _gatewayIds.OrderBy(x => x))
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
        localMessageQueueService.SendMessage -= LocalMessageQueueService_SendMessage;
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

    public async Task FillGatewayIds()
    {
        using var scope = services.CreateScope();
        var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
        var ids = new HashSet<long>(await registrationService.GetGatewaysCached());
        foreach (var id in _options.GatewayNodeIds)
        {
            ids.Add(id);
        }
        _gatewayIds = ids;
    }


    private async Task HandleMeshtasticMessage(DataEventArgs<ServiceEnvelope> msg)
    {
        IServiceScope scope = null;
        try
        {
            if (msg.Data == null || msg.Data.Packet == null)
            {
                logger.LogWarning("Received Meshtastic message with null data");
                return;
            }

            if (!MeshtasticService.TryParseDeviceId(msg.Data.GatewayId, out var gatewayId))
            {
                logger.LogWarning("Received Meshtastic message with invalid gateway ID format: {GatewayId}", msg.Data.GatewayId);
                return;
            }

            if (!_gatewayIds.Contains(gatewayId))
            {
                logger.LogWarning("Received Meshtastic message from unregistered gateway ID {GatewayId}", gatewayId);
                return;
            }

            UpdateGatewayLastSeen(gatewayId);

            if (meshtasticService.IsLinkTrace(msg.Data))
            {
                scope ??= services.CreateScope();
                await SaveLinkTrace(scope, gatewayId, msg.Data);
                return;
            }

            if (!meshtasticService.TryStoreNoDup(msg.Data.Packet.Id))
            {
                meshtasticService.AddStat(new MeshStat
                {
                    DupsIgnored = 1,
                });
                logger.LogDebug("Duplicate Meshtastic message received, ignoring. Id: {Id}", msg.Data.Packet.Id);
                return;
            }

            if (meshtasticService.TryBridge(msg.Data, _gatewayIds))
            {
                return;
            }

            var packetFromTo = MeshtasticService.GetPacketAddresses(msg.Data);
            Device device = null;
            RegistrationService registrationService = null;
            var recipients = new List<IRecipient>(8);
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
            else if (!packetFromTo.IsPkiEncrypted)
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

            await PerhapsSaveLinkTrace(scope, gatewayId, res.msg);

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

    private async ValueTask PerhapsSaveLinkTrace(
        IServiceScope scope,
        long gatewayId,
        MeshMessage msg)
    {
        if (msg.MessageType == MeshMessageType.DeviceMetrics
            && (msg.DeviceId == gatewayId || _gatewayIds.Contains(msg.DeviceId)))
        {
            await SaveLinkTrace(
                scope: scope,
                packetId: msg.Id,
                gatewayId: gatewayId,
                deviceId: msg.DeviceId,
                hopStart: (uint)msg.HopStart,
                hopLimit: (uint)msg.HopLimit,
                cachePacketId: true);
        }
    }



    private async ValueTask SaveLinkTrace(
      IServiceScope scope,
      long packetId,
      long gatewayId,
      long deviceId,
      uint hopStart,
      uint hopLimit,
      bool cachePacketId)
    {
        var analyticsService = scope.ServiceProvider.GetService<AnalyticsService>();
        if (analyticsService != null)
        {
            if (cachePacketId)
            {
                meshtasticService.StoreGatewayLinkTraceStepZero(packetId);
            }
            meshtasticService.AddStat(new MeshStat
            {
                LinkTraces = 1,
            });

            byte? step = null;
            if (deviceId == gatewayId)
            {
                step = 0;
            }
            else
            {
                var hopsUsed = MeshtasticService.TryGetUsedHops(hopStart, hopLimit);
                if (hopsUsed.HasValue)
                {
                    step = (byte)(1 + hopsUsed);
                }
            }

            var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
            var toDevice = await registrationService.GetDeviceAsync(gatewayId);

            if (toDevice == null 
                || toDevice.Latitude == null
                || toDevice.Longitude == null)
            {
                return;
            }

            var fromDevice = await registrationService.GetDeviceAsync(deviceId);
            if (fromDevice == null
                || fromDevice.Latitude == null
                || fromDevice.Longitude == null)
            {
                return;
            }

            await analyticsService.RecordLinkTrace(
                packetId: packetId,
                fromGatewayId: deviceId,
                toGatewayId: gatewayId,
                step: step,
                toLatitude: toDevice.Latitude.Value,
                toLongitude: toDevice.Longitude.Value,
                fromLatitude: fromDevice.Latitude.Value,
                fromLongitude: fromDevice.Longitude.Value);
        }
    }

    private async Task SaveLinkTrace(IServiceScope scope, long gatewayId, ServiceEnvelope env)
    {
        await SaveLinkTrace(
            scope: scope,
            packetId: env.Packet.Id,
            gatewayId: gatewayId,
            deviceId: env.Packet.From,
            hopStart: env.Packet.HopStart,
            hopLimit: env.Packet.HopLimit,
            cachePacketId: false);
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
            if (botService.GatewayListChanged)
            {
                await FillGatewayIds();
            }
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
