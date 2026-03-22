using Meshtastic.Protobufs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Models;
using System.Collections.Concurrent;
using System.Timers;
using TBot.Analytics;
using TBot.Bot;
using TBot.Database.Models;
using TBot.Models;
using TBot.Models.MeshMessages;
using TBot.Models.Queue;

namespace TBot;

public class MessageLoopService(
    ILogger<MessageLoopService> logger,
    LocalMessageQueueService localMessageQueueService,
    MqttService mqttService,
    MapMqttService mapMqttService,
    MeshtasticService meshtasticService,
    IOptions<TBotOptions> options,
    IServiceProvider services,
    SimpleScheduler scheduler) : IHostedService
{
    private const int GatewayActivityRefreshEveryHours = 1;
    private const int CheckGatewayNodeInfoLastSeenAfterMinutes = 60;
    private readonly TBotOptions _options = options.Value;
    private System.Timers.Timer _serviceInfoTimer;
    private DateTime _lastVirtualNodeInfoSent = DateTime.MinValue;
    private DateTime _started;
    private DateTime _lastGatewayCleanup = DateTime.MinValue;
    private readonly ConcurrentDictionary<long, DateTime> _gatewayLastSeen = new();

    private BlockingCollection<AckMessage> _ackQueue;
    private readonly SemaphoreSlim _ackQueueSemaphore = new(0);
    private Task _ackWorker;
    private Dictionary<long, int> _gatewayNetworkIds;
    private Dictionary<int, PublicChannel> _primaryChannels;
    private HashSet<long> _registeredGatewayIds;
    private HashSet<long> _newGatewayIds;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _started = DateTime.UtcNow;
        using var scope = services.CreateScope();
        await FillGatewayIds(scope);
        await FillPrimaryChannels(scope);
        mqttService.TelegramUpdateReceivedAsync += HandleTelegramUpdate;
        mqttService.MeshtasticMessageReceivedAsync += HandleMeshtasticMessage;
        mqttService.MessageSent += HandleMessageSent;
        using var cts = new CancellationTokenSource(30_000);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
        await mqttService.ConnectAsync(linkedCts.Token);
        mapMqttService.MeshtasticMessageReceivedAsync += HandleMapMqttTelemetryAsync;
        await mapMqttService.StartAsync(cancellationToken);
        localMessageQueueService.SendMessage += LocalMessageQueueService_SendMessage;
        localMessageQueueService.Start();
        StartServiceInfoInfoTimer();

#pragma warning disable IDE0028 // Simplify collection initialization
        _ackQueue = new BlockingCollection<AckMessage>();
#pragma warning restore IDE0028 // Simplify collection initialization
        _ackWorker = AckWorker(_ackQueue);
        await SendVirtualNodeInfo(scope);
        await PublishStats(scope);
    }

    private async Task SendVirtualNodeInfo(IServiceScope scope)
    {
        _lastVirtualNodeInfoSent = DateTime.UtcNow;
        var regService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
        var primaryChannels = await regService.GetAllPrimaryChannelsCached();
        foreach (var channel in primaryChannels.Values)
        {
            meshtasticService.SendVirtualNodeInfo(channel.Name, channel);
        }
    }

    private async Task HandleMessageSent(DataEventArgs<long> args)
    {
        using var scope = services.CreateScope();
        var botService = scope.ServiceProvider.GetRequiredService<MeshtasticBotMsgStatusTracker>();
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
            var scope = services.CreateScope();
            if ((DateTime.UtcNow - _lastVirtualNodeInfoSent).TotalSeconds >= _options.SentTBotNodeInfoEverySeconds)
            {
                await SendVirtualNodeInfo(scope);
            }

            await PublishStats(scope);

            if (_options.InactiveGatewayCleanupDays > 0
                && (DateTime.UtcNow - _lastGatewayCleanup).TotalHours >= GatewayActivityRefreshEveryHours)
            {
                _lastGatewayCleanup = DateTime.UtcNow;
                await CheckGatewayActivity(scope);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in ServiceInfo timer");
        }
    }


    private async Task CheckGatewayActivity(IServiceScope scope)
    {
        try
        {
            var threshold = DateTime.UtcNow.AddDays(-_options.InactiveGatewayCleanupDays);
            var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
            await RefreshGatewayActivityInDb(registrationService);

            var demoted = await registrationService.UnregisterInactiveGatewaysAsync(threshold);
            if (demoted.Count == 0)
            {
                return;
            }

            logger.LogInformation("Auto-demoted {Count} inactive gateway(s): {Ids}",
                demoted.Count,
                string.Join(", ", demoted.Select(d => MeshtasticService.GetMeshtasticNodeHexId(d.DeviceId))));

            await FillGatewayIds(scope);

            var botService = scope.ServiceProvider.GetRequiredService<TgBotService>();
            foreach (var (deviceId, nodeName) in demoted)
            {
                await botService.NotifyGatewayDemotedDueToInactivity(deviceId, nodeName);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during inactive gateway cleanup");
        }
    }

    private async Task RefreshGatewayActivityInDb(RegistrationService registrationService)
    {
        foreach (var gatewayId in _registeredGatewayIds)
        {
            if (_gatewayLastSeen.TryGetValue(gatewayId, out var lastSeen))
            {
                if ((DateTime.UtcNow - lastSeen).TotalHours < GatewayActivityRefreshEveryHours)
                {
                    await registrationService.UpdateGatewayLastSeenAsync(gatewayId, lastSeen);
                }
            }
        }
    }

    private Task LocalMessageQueueService_SendMessage(DataEventArgs<QueuedMessage> arg)
    {
        var relayThroughGatewayId = arg.Data.RelayThroughGatewayId;
        if (relayThroughGatewayId.HasValue && !_gatewayNetworkIds.ContainsKey(relayThroughGatewayId.Value))
        {
            relayThroughGatewayId = null;
        }

        meshtasticService.StoreNoDup(arg.Data.Message.Packet.Id);

        return mqttService.PublishMeshtasticMessage(
            arg.Data.NetworkId,
            arg.Data.Message,
            relayThroughGatewayId);
    }

    private async Task PublishStats(IServiceScope scope)
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
        var stat = new Dictionary<string, DateTime?>(_gatewayNetworkIds.Count);
        foreach (var gwId in _gatewayNetworkIds.Keys.OrderBy(x => x))
        {
            if (!_gatewayLastSeen.TryGetValue(gwId, out var lastSeen))
            {
                lastSeen = DateTime.MinValue;
            }
            if ((utcNow - lastSeen).TotalMinutes > CheckGatewayNodeInfoLastSeenAfterMinutes)
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
        mqttService.TelegramUpdateReceivedAsync -= HandleTelegramUpdate;
        mqttService.MeshtasticMessageReceivedAsync -= HandleMeshtasticMessage;
        mqttService.MessageSent -= HandleMessageSent;
        mapMqttService.MeshtasticMessageReceivedAsync -= HandleMapMqttTelemetryAsync;
        await mapMqttService.DisposeAsync();
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
                            var botService = scope.ServiceProvider
                                .GetRequiredService<MeshtasticBotMsgStatusTracker>();
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

    public async Task FillPrimaryChannels(IServiceScope scope)
    {
        var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
        var primaryChannels = await registrationService.GetAllPrimaryChannelsCached();
        _primaryChannels = new Dictionary<int, PublicChannel>(primaryChannels);
    }

    public async Task FillGatewayIds(IServiceScope scope)
    {
        var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
        var registeredGateways = await registrationService.GetGatewaysCached();
        var ids = new Dictionary<long, int>();
        foreach (var gw in registeredGateways)
        {
            ids.Add(gw.Key, gw.Value.NetworkId);
        }
        _gatewayNetworkIds = ids;
        _registeredGatewayIds = [.. registeredGateways.Keys];
        _newGatewayIds = [.. registeredGateways.Values
                    .Where(x => x.LastSeen == null)
                    .Select(x => x.DeviceId)];
    }


    private async Task HandleMeshtasticMessage(DataEventArgs<NetworkServiceEnvelope> msg)
    {
        IServiceScope scope = null;
        try
        {
            if (msg.Data == null || msg.Data.Envelope.Packet == null)
            {
                logger.LogWarning("Received Meshtastic message with null data");
                return;
            }

            if (!MeshtasticService.TryParseDeviceId(msg.Data.Envelope.GatewayId, out var gatewayId))
            {
                logger.LogWarning("Received Meshtastic message with invalid gateway ID format: {GatewayId}", msg.Data.Envelope.GatewayId);
                return;
            }

            if (gatewayId == _options.MeshtasticNodeId)
            {
                //This is a message from our own virtual node, ignore early
                return;
            }

            if (!_gatewayNetworkIds.TryGetValue(gatewayId, out var networkId))
            {
                logger.LogWarning("Received Meshtastic message from unregistered gateway ID {GatewayId}", gatewayId);
                return;
            }

            if (msg.Data.NetworkId == null || msg.Data.NetworkId != networkId)
            {
                logger.LogWarning("Received Meshtastic message with invalid network ID {NetworkId} from gateway ID {GatewayId}", msg.Data.NetworkId, gatewayId);
                return;
            }


            var env = msg.Data.Envelope;

            UpdateGatewayLastSeen(gatewayId);

            if (mapMqttService.UplinkEnabled
                && meshtasticService.IsUplinkPacket(env))
            {
                await UplinkToMap(env);
            }

            if (meshtasticService.IsLinkTrace(env))
            {
                scope ??= services.CreateScope();
                await SaveLinkTrace(scope, networkId, gatewayId, env);
                return;
            }

            if (!meshtasticService.TryStoreNoDup(env.Packet.Id))
            {
                meshtasticService.AddStat(new MeshStat
                {
                    DupsIgnored = 1,
                });
                return;
            }

            if (meshtasticService.TryBridge(env, _gatewayNetworkIds))
            {
                return;
            }

            var packetFromTo = MeshtasticService.GetPacketAddresses(env);
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
                recipients.AddRange(
                    await registrationService.GetPublicChannelKeysByHashCached(
                        networkId, packetFromTo.XorHash));
                var channelRecipients =
                    await registrationService.GetChannelKeysByHashCached(networkId, packetFromTo.XorHash);
                recipients.AddRange(channelRecipients);
            }

            var res = meshtasticService.TryDecryptMessage(env, recipients);
            if (res.msg != null)
            {
                res.msg.NetworkId = networkId;
            }
            if (!res.success)
            {
                await UplinkToMap(env);
                return;
            }

            if (res.msg.MessageType == MeshMessageType.AckMessage)
            {
                EnqueueAckMessage((AckMessage)res.msg);
                return;
            }

            scope ??= services.CreateScope();

            var t1 = PerhapsSaveLinkTrace(scope, gatewayId, res.msg);
            var t2 = PerhapsUplinkToMap(env, res.msg);

            await Task.WhenAll(t1.AsTask(), t2.AsTask());

            if (res.msg.MessageType == MeshMessageType.Unknown)
            {
                return;
            }

            var botService = scope.ServiceProvider.GetRequiredService<MeshtasticBotService>();
            await botService.ProcessInboundMeshtasticMessage(res.msg, device);
            if (botService.TrackedMessages != null)
            {
                ScheduleStatusResolve(botService.TrackedMessages);
            }
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

    private async ValueTask PerhapsUplinkToMap(
        ServiceEnvelope data,
        MeshMessage msg)
    {
        if (!mapMqttService.UplinkEnabled)
        {
            return;
        }

        if (!msg.OkToMqtt)
        {
            return;
        }

        await UplinkToMap(data);
    }

    private async ValueTask UplinkToMap(ServiceEnvelope data)
    {
        meshtasticService.MarkUplinkPacket(data.Packet.Id);
        await mapMqttService.PublishMeshtasticMessage(data);
    }

    private async ValueTask PerhapsSaveLinkTrace(
        IServiceScope scope,
        long gatewayId,
        MeshMessage msg)
    {
        if (msg.MessageType == MeshMessageType.DeviceMetrics
            && (msg.DeviceId == gatewayId || _gatewayNetworkIds.ContainsKey(msg.DeviceId)))
        {
            await SaveLinkTrace(
                scope: scope,
                packetId: msg.Id,
                networkId: msg.NetworkId,
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
      int networkId,
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
                networkId: networkId,
                toGatewayId: gatewayId,
                step: step,
                toLatitude: toDevice.Latitude.Value,
                toLongitude: toDevice.Longitude.Value,
                fromLatitude: fromDevice.Latitude.Value,
                fromLongitude: fromDevice.Longitude.Value);
        }
    }

    private async Task SaveLinkTrace(IServiceScope scope, int networkId, long gatewayId, ServiceEnvelope env)
    {
        await SaveLinkTrace(
            scope: scope,
            packetId: env.Packet.Id,
            gatewayId: gatewayId,
            networkId: networkId,
            deviceId: env.Packet.From,
            hopStart: env.Packet.HopStart,
            hopLimit: env.Packet.HopLimit,
            cachePacketId: false);
    }

    private void UpdateGatewayLastSeen(long gatewayId)
    {
        var now = DateTime.UtcNow;
        _gatewayLastSeen.AddOrUpdate(gatewayId, now, (key, oldValue) => oldValue > now ? oldValue : now);
        if (_newGatewayIds.Remove(gatewayId))
        {
            //Send notification about new gateway seen for the first time
            Task.Run(async () =>
            {
                try
                {
                    using var scope = services.CreateScope();
                    var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
                    await registrationService.UpdateGatewayLastSeenAsync(gatewayId, now);
                    var botService = scope.ServiceProvider.GetRequiredService<TgBotService>();
                    await botService.NotifyNewGatewaySeen(gatewayId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error notifying about new gateway seen");
                }
            });
        }
    }

    private async Task HandleMapMqttTelemetryAsync(DataEventArgs<ServiceEnvelope> args)
    {
        try
        {
            var env = args.Data;
            if (env?.Packet == null
                || String.IsNullOrEmpty(env.GatewayId)
                || !MeshtasticService.TryParseDeviceId(env.GatewayId, out var gatewayId))
                return;

            if (_gatewayNetworkIds.TryGetValue(gatewayId, out var networkId))
            {
                //We should not process telemetry messages from known gateways here, as they are already processed in HandleMeshtasticMessage.
                return;
            }

            long deviceId = env.Packet.From;

            var primaryChannel = _primaryChannels.GetValueOrDefault(networkId);
            if (primaryChannel == null)
            {
                return;
            }

            var (success, msg) = meshtasticService.TryDecryptMessage(env, [primaryChannel]);

            if (!success
                || msg.MessageType != MeshMessageType.DeviceMetrics)
            {
                return;
            }

            if (meshtasticService.IsLinkTrace(env)
                && meshtasticService.TryStoreLinkTraceGatewayNoDup(env.Packet.Id, gatewayId))
            {
                using var scope = services.CreateScope();
                await SaveLinkTrace(scope, networkId, gatewayId, env);
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MapMqtt: error processing telemetry packet");
        }
    }

    private async Task HandleTelegramUpdate(DataEventArgs<string> msg)
    {
        try
        {
            using var scope = services.CreateScope();
            var botService = scope.ServiceProvider.GetRequiredService<TgBotService>();
            await botService.ProcessInboundTelegramUpdate(msg.Data);
            await HandleBotResult(scope, botService);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling MQTT message");
        }
    }

    private async Task HandleBotResult(IServiceScope scope, TgBotService botService)
    {
        ScheduleStatusResolve(botService.TrackedMessages);

        bool updateChannelsInMapService = false;
        if (botService.NetworkPublicChannelsChanged != null
            && botService.NetworkPublicChannelsChanged.Count != 0)
        {
            await FillPrimaryChannels(scope);
            updateChannelsInMapService = true;

        }
        if (botService.NetworkGatewayListChanged != null
            && botService.NetworkGatewayListChanged.Count != 0)
        {
            await FillGatewayIds(scope);
        }

        if (botService.NetworksUpdated
            || updateChannelsInMapService)
        {
            await mapMqttService.FillNetworks(scope);
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
            var botService = scope.ServiceProvider.GetRequiredService<MeshtasticBotMsgStatusTracker>();
            await botService.ResolveMessageStatus(telegramChatId, telegramMessageId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing Meshtastic message status");
        }
    }
}
