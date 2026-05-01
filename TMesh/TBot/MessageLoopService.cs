using Google.Protobuf;
using Google.Protobuf.Collections;
using Meshtastic.Protobufs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Models;
using System.Collections.Concurrent;
using System.Text.Json;
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
    SimpleScheduler scheduler,
    BotCache botCache) : IHostedService
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
        await botCache.Start(scope);
        mqttService.TelegramUpdateReceivedAsync += HandleTelegramUpdate;
        mqttService.MeshtasticMessageReceivedAsync += HandleMeshtasticMessage;
        mqttService.MessageSent += HandleMessageSent;
        using var cts = new CancellationTokenSource(30_000);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
        await mqttService.ConnectAsync(linkedCts.Token);
        mapMqttService.MeshtasticMessageReceivedAsync += HandleMapMqttMessageAsync;
        await mapMqttService.StartAsync(scope, cancellationToken);
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
        var nodeInfoChannels = await regService.GetAllNodeInfoChannelsCached();
        foreach (var channel in nodeInfoChannels)
        {
            meshtasticService.SendVirtualNodeInfo(channel.Name, channel, hopLimit: int.MaxValue);
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
        _serviceInfoTimer = new System.Timers.Timer(120 * 1000);
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

            await SendScheduledMessages(scope);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in ServiceInfo timer");
        }
    }

    private async Task SendScheduledMessages(IServiceScope scope)
    {
        try
        {
            var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
            var now = DateTime.UtcNow;

            await registrationService.ApplyScheduledMessageStatusUpdatesAsync(now);

            var due = await registrationService.GetDueScheduledMessagesAsync(now);
            if (due.Count == 0)
                return;

            var sent = new List<ScheduledMessage>(due.Count);
            foreach (var (msg, channel) in due)
            {
                if (channel == null)
                {
                    logger.LogWarning("ScheduledMessage #{Id}: public channel #{ChannelId} not found, skipping", msg.Id, msg.PublicChannelId);
                    continue;
                }

                try
                {
                    var newMsgId = MeshtasticService.GetNextMeshtasticMessageId();
                    botCache.StoreMessageSentByOurNode(newMsgId);
                    meshtasticService.SendPublicTextMessage(
                        newMsgId,
                        msg.Text,
                        relayGatewayId: null,
                        hopLimit: int.MaxValue,
                        publicChannelName: channel.Name,
                        recipient: channel);

                    sent.Add(msg);
                    logger.LogInformation("ScheduledMessage #{Id} sent to channel \"{Channel}\"", msg.Id, channel.Name);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error sending ScheduledMessage #{Id}", msg.Id);
                }
            }

            if (sent.Count > 0)
            {
                await registrationService.UpdateScheduledMessageLastSentAsync(sent, now);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing scheduled messages");
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

    private async Task LocalMessageQueueService_SendMessage(DataEventArgs<QueuedMessage> arg)
    {
        var relayThroughGatewayId = arg.Data.RelayThroughGatewayId;
        if (relayThroughGatewayId.HasValue && !_gatewayNetworkIds.ContainsKey(relayThroughGatewayId.Value))
        {
            relayThroughGatewayId = null;
        }

        meshtasticService.StoreNoDup(arg.Data.Message.Packet.Id);

        await mqttService.PublishMeshtasticMessage(
            arg.Data.NetworkId,
            arg.Data.Message,
            relayThroughGatewayId);

        if (mapMqttService.UplinkEnabled
            && !arg.Data.Message.Packet.ViaMqtt)
        {
            await UplinkToMap(arg.Data.NetworkId, arg.Data.Message);
        }
    }

    private async Task PublishStats(IServiceScope scope)
    {
        var now = DateTime.UtcNow;
        var min5ago = now.AddMinutes(-5);
        var min15ago = now.AddMinutes(-15);
        var hour1ago = now.AddHours(-1);
        var hours24ago = now.AddHours(-24);

        var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();

        // Include all networks from database, not just those with stats
        var networks = await registrationService.GetNetworksCached();

        var allStats = new List<NetworkStats>(networks.Count);
        var analyticsService = scope.ServiceProvider.GetService<AnalyticsService>();

        foreach (var network in networks)
        {
            var networkStats = new NetworkStats
            {
                Id = network.Id,
                Name = network.Name,
                Mesh5Min = meshtasticService.AggregateStartFrom(network.Id, min5ago),
                Mesh15Min = meshtasticService.AggregateStartFrom(network.Id, min15ago),
                Mesh1Hour = meshtasticService.AggregateStartFrom(network.Id, hour1ago),
                DeviceChatRegistrations = await registrationService.GetDeviceRegistrationsCountByNetwork(network.Id),
                ChannelChatRegistrations = await registrationService.GetChannelRegistrationsCountByNetwork(network.Id),
                Devices = await registrationService.GetDevicesCountByNetwork(network.Id),
                Devices24h = await registrationService.GetActiveDevicesCountByNetwork(network.Id, hours24ago),
                MfVoteDevices24h = await registrationService.GetActiveDevicesCountByNetworkAndPrefix(network.Id, hours24ago, "[MF]"),
                LfVoteDevices24h = await registrationService.GetActiveDevicesCountByNetworkAndPrefix(network.Id, hours24ago, "[LF]"),
                GatewaysLastSeen = await GetGatewaysLastSeenStatByNetwork(now, registrationService, network.Id)
            };

            networkStats.NoVoteDevices24h = networkStats.Devices24h
                - networkStats.MfVoteDevices24h
                - networkStats.LfVoteDevices24h;

            if (analyticsService != null && network.SaveAnalytics)
            {
                networkStats.TelemetrySaved24H = await analyticsService.GetStatisticsByNetwork(network.Id, hours24ago);
            }

            allStats.Add(networkStats);
        }

        var botStats = new BotStats
        {
            Networks = allStats,
            LastUpdate = now,
            Started = _started,
            TgChats = await registrationService.GetTelegramChatsCount(),
            ApprovedChannels = await registrationService.GetApprovedChannelsCount(),
            ApprovedDevices = await registrationService.GetApprovedDevicesCount(),
            ActiveChatSessions = await registrationService.GetActiveChatSessionsCount(),
            MapMqtt = mapMqttService.GetStatus(),
        };

        await mqttService.PublishStatus(botStats);
    }

    private async ValueTask<Dictionary<string, DateTime?>> GetGatewaysLastSeenStatByNetwork(DateTime utcNow, RegistrationService regService, int networkId)
    {
        var stat = new Dictionary<string, DateTime?>();
        var gatewaysInNetwork = _gatewayNetworkIds.Where(g => g.Value == networkId).Select(g => g.Key);

        foreach (var gwId in gatewaysInNetwork.OrderBy(x => x))
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
        mapMqttService.MeshtasticMessageReceivedAsync -= HandleMapMqttMessageAsync;
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
                && !env.Packet.ViaMqtt
                && meshtasticService.IsUplinkPacket(env))
            {
                await UplinkToMap(networkId, env);
            }

            await ProcessPacket(env, networkId, gatewayId, isTMeshGateway: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling TMesh gateway MQTT message");
        }
    }

    public async Task ProcessPacket(
        ServiceEnvelope env,
        int networkId,
        long tmeshOrMapGatewayId,
        bool isTMeshGateway)
    {
        IServiceScope scope = null;

        try
        {
            if (meshtasticService.IsPreviouslySeenLinkTrace(env))
            {
                scope ??= services.CreateScope();
                await SaveLinkTrace(scope, networkId, tmeshOrMapGatewayId, env);
                return;
            }
            else if (meshtasticService.IsPreviouslySeenNodeInfo(env))
            {
                scope ??= services.CreateScope();
                await SaveNodeInfoFromEnvelope(scope, networkId, tmeshOrMapGatewayId, env);
                return;
            }

            if (!meshtasticService.TryStoreNoDup(env.Packet.Id))
            {
                meshtasticService.AddStat(new MeshStat
                {
                    NetworkId = networkId,
                    DupsIgnored = 1,
                });
                return;
            }

            if (TryBridge(env, tmeshOrMapGatewayId))
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
                if (isTMeshGateway)
                {
                    res.msg.TMeshGatewayId = res.msg.GatewayId;
                }

                res.msg.NetworkId = networkId;
                if (_options.DebugPacketsViaMqtt && isTMeshGateway)
                {
                    mqttService.PublishMessageToDebug(res.msg);
                }
            }
            if (!res.success)
            {
                if (isTMeshGateway && !env.Packet.ViaMqtt)
                {
                    await UplinkToMap(networkId, env);
                }
                return;
            }

            if (res.msg != null
                && res.msg.MessageType == MeshMessageType.NodeInfo
                && res.msg is NodeInfoMessage nim)
            {
                nim.Packet.GatewayId = (uint)tmeshOrMapGatewayId;
                nim.Packet.IsTMeshGateway = isTMeshGateway;

                if (nim.DeviceId == _options.MeshtasticNodeId)
                {
                    var publicKeyBase64 = Convert.ToBase64String(nim.PublicKey);
                    if (publicKeyBase64 != _options.MeshtasticPublicKeyBase64
                        || nim.NodeName != _options.MeshtasticNodeNameLong)
                    {
                        logger.LogError("Security warning: NodeInfo message from self - {nim}. Public key - {key}", JsonSerializer.Serialize(nim), Convert.ToBase64String(nim.PublicKey));
                    }
                }
                return;
            }

            if (res.msg.MessageType == MeshMessageType.AckMessage)
            {
                EnqueueAckMessage((AckMessage)res.msg);
                return;
            }

            scope ??= services.CreateScope();

            var t1 = PerhapsSaveForAnalytics(scope, tmeshOrMapGatewayId, res.msg);

            var t2 = isTMeshGateway
                ? PerhapsUplinkToMap(env, res.msg)
                : ValueTask.CompletedTask;

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
        finally
        {
            scope?.Dispose();
        }
    }

    private bool TryBridge(ServiceEnvelope envelope, long gatewayId)
    {
        if (envelope.Packet == null || envelope.Packet.PayloadVariantCase != MeshPacket.PayloadVariantOneofCase.Encrypted)
        {
            return false;
        }

        var senderDeviceId = envelope.Packet.From;
        var receiverDeviceId = envelope.Packet.To;

        var senderIsGateway = _gatewayNetworkIds.TryGetValue(senderDeviceId, out var senderNetworkId);
        var receiverIsGateway = _gatewayNetworkIds.TryGetValue(receiverDeviceId, out var receiverNetworkId);

        if (_options.BridgeDirectMessagesToGateways
               && senderDeviceId != receiverDeviceId
               && senderDeviceId != _options.MeshtasticNodeId
               && (senderIsGateway || receiverIsGateway)
               && (receiverIsGateway || (_options.BridgeAllowedExtraNodeIds != null
                    && _options.BridgeAllowedExtraNodeIds.Contains(receiverDeviceId)))
               && (senderIsGateway || (_options.BridgeAllowedExtraNodeIds != null
                    && _options.BridgeAllowedExtraNodeIds.Contains(senderDeviceId)))
               && gatewayId != _options.MeshtasticNodeId)
        {
            long outGoingGatewayId;
            if (receiverIsGateway)
            {
                outGoingGatewayId = receiverDeviceId;
            }
            else
            {
                var ids = botCache.GetDeviceGateway(receiverDeviceId);
                if (ids == null || !_gatewayNetworkIds.TryGetValue(ids.GatewayId, out receiverNetworkId))
                {
                    return false;
                }
                outGoingGatewayId = ids.GatewayId;
            }

            var primaryChannel = _primaryChannels.GetValueOrDefault(receiverNetworkId);
            if (primaryChannel == null)
            {
                return false;
            }

            var decryptRes = meshtasticService.TryDecryptPskTraceRoute(envelope, primaryChannel);

            meshtasticService.AddStat(new MeshStat
            {
                NetworkId = receiverNetworkId,
                BridgeDirectMessagesToGateways = 1,
            });

            if (!senderIsGateway)
            {
                botCache.StoreDeviceGateway(senderDeviceId, gatewayId, _options.OutgoingMessageHopLimit);
            }

            ServiceEnvelope outgoing;
            if (!decryptRes.success || decryptRes.msg == null || decryptRes.msg.MessageType != MeshMessageType.TraceRoute)
            {
                outgoing = envelope.Clone();
                outgoing.GatewayId = MeshtasticService.GetMeshtasticNodeHexId(_options.MeshtasticNodeId);
                meshtasticService.QueueMessage(
                    outgoing,
                    receiverNetworkId,
                    MessagePriority.High,
                    outGoingGatewayId);
            }
            else
            {
                logger.LogInformation("Bridging direct message from {Sender} to {Receiver} via trace route injection", MeshtasticService.GetMeshtasticNodeHexId(senderDeviceId), MeshtasticService.GetMeshtasticNodeHexId(receiverDeviceId));

                meshtasticService.InjectOurNodeInTraceRouteAndSend(
                    (TraceRouteMessage)decryptRes.msg,
                    receiverDeviceId,
                    primaryChannel,
                    primaryChannel.Name,
                    gatewayId,
                    outGoingGatewayId);
            }
            return true;
        }
        return false;
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

        if (msg.ViaMqtt)
        {
            //No uplink for messages that were already received via MQTT, to avoid loops and duplicates in the map service
            return;
        }

        await UplinkToMap(msg.NetworkId, data);
    }

    private async ValueTask UplinkToMap(int networkId, ServiceEnvelope data)
    {
        meshtasticService.MarkUplinkPacket(data.Packet.Id);

        if (data.Packet.ViaMqtt)
        {
            throw new Exception("Trying to uplink a packet that was received via MQTT, this should not happen");
        }

        await mapMqttService.PublishMeshtasticMessage(networkId, data);
    }

    private async ValueTask PerhapsSaveForAnalytics(
        IServiceScope scope,
        long gatewayId,
        MeshMessage msg)
    {
        try
        {
            if (msg.MessageType == MeshMessageType.DeviceMetrics
                && _gatewayNetworkIds.ContainsKey(msg.DeviceId))
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
            else if (msg.MessageType == MeshMessageType.NodeInfo)
            {
                await SaveNodeInfo(scope, (NodeInfoMessage)msg);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in PerhapsSaveForAnalytics");
        }
    }

    private async Task SaveNodeInfoFromEnvelope(IServiceScope scope, int networkId, long gatewayId, ServiceEnvelope env)
    {
        var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
        var primaryChannel = await registrationService.GetNetworkPrimaryChannelCached(networkId);
        if (primaryChannel == null) {
            logger.LogWarning("Primary channel not found for network ID {NetworkId}, cannot save node info", networkId);
            return;
        }

        var res = meshtasticService.TryDecryptMessage(env, [primaryChannel]);
        if (!res.success || res.msg is not NodeInfoMessage nodeInfoMsg)
            return;

        await SaveNodeInfo(scope, nodeInfoMsg);
    }

    private async ValueTask SaveNodeInfo(IServiceScope scope, NodeInfoMessage msg)
    {
        var analyticsService = scope.ServiceProvider.GetService<AnalyticsService>();
        if (analyticsService == null)
        {
            return;
        }

        if (msg.DecodedBy == null || !msg.DecodedBy.IsPublicChannel)
        {
            return;
        }

        var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
        var network = await registrationService.GetNetwork(msg.NetworkId);
        if (network == null || !network.SaveAnalytics)
        {
            return;
        }

        var primaryChannel = await registrationService.GetNetworkPrimaryChannelCached(msg.NetworkId);
        if (primaryChannel == null || primaryChannel.Id != msg.DecodedBy.RecipientPublicChannelId)
        {
            return;
        }

        meshtasticService.MarkAsNodeInfo(msg.Id);

        await analyticsService.SaveNodeInfo(msg.Packet,
            msg.NodeInfo,
            new Analytics.Models.PacketBody
            {
                Body = msg.RawPacketEnvelope.ToByteArray(),
            });

        return;
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
            var registrationService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
            var network = await registrationService.GetNetwork(networkId);
            if (network == null || !network.SaveAnalytics)
            {
                return;
            }

            if (cachePacketId)
            {
                meshtasticService.MarkAsLinkTrace(packetId);
            }
            meshtasticService.AddStat(new MeshStat
            {
                NetworkId = networkId,
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

    private async Task HandleMapMqttMessageAsync(DataEventArgs<NetworkServiceEnvelope> args)
    {
        try
        {
            var env = args.Data.Envelope;
            if (env?.Packet == null
                || args.Data.NetworkId == null
                || String.IsNullOrEmpty(env.GatewayId)
                || !MeshtasticService.TryParseDeviceId(env.GatewayId, out var mapGatewayId))
                return;

            if (_gatewayNetworkIds.TryGetValue(mapGatewayId, out _))
            {
                //We should not process telemetry messages from known gateways here, as they are already processed in HandleMeshtasticMessage.
                return;
            }

            await ProcessPacket(env, args.Data.NetworkId.Value, mapGatewayId, isTMeshGateway: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Map Mqtt processing error");
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
        if (msgStatuses == null)
        {
            return;
        }

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
