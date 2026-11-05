using Google.Protobuf;
using Meshtastic.Protobufs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Packets;
using Shared.Models;
using System.Text;
using TBot.Database.Models;
using TBot.Models;
using TBot.Models.Uplink;

namespace TBot
{
    public class MapMqttService(
        ILogger<MapMqttService> logger,
        IOptions<TBotOptions> options,
        MqttClientFactory mqttClientFactory) : IAsyncDisposable
    {
        private const int ConnectMillisecondsTtl = 30_000;
        private const int ReconnectMillisecondsDelay = 10000;
#if DEBUG
        const string ClientId = "TMeshDebug";
#else
        const string ClientId = "TMesh";
#endif

        const string NetworkShortNameToken = "{{NetworkShortName}}";
        private CancellationTokenSource _connectionCts = new();
        private readonly TBotOptions _options = options.Value;
        private Dictionary<int, NetworkShortInfo> _networks;
        private ILookup<string, int> _networkByShortName;
        private readonly List<Task> _connectTasks = new List<Task>();

        private readonly List<(IMqttClient mqttClient, MapMqttServerOptions server)> _clients = [];

        public ServerStatus[] GetStatus()
        {
            List<ServerStatus> status = [];

            (IMqttClient mqttClient, MapMqttServerOptions server)[] snapshot;
            lock (_clients)
            {
                snapshot = [.. _clients];
            }

            return [.. snapshot.Select(x =>
            {
                var s = x.mqttClient.IsConnected ? "Connected" : "Disconnected";
                var serverId = new StringBuilder();
                if (x.server.AnalyticsDownlinkEnabled)
                {
                    serverId.Append('d');
                }
                if (x.server.UplinkMode != UplinkMode.Disabled)
                {
                    serverId.Append('u');
                    serverId.Append((int)x.server.UplinkMode);
                }
                serverId.Append('-');
                serverId.Append(x.server.Address);
                return new ServerStatus
                {
                    ServerID = serverId.ToString(),
                    Status = s
                };
            })];
        }

        /// <summary>Raised when a PKI-encrypted telemetry packet from a TMesh gateway is received.</summary>
        public event Func<DataEventArgs<NetworkServiceEnvelope>, Task> MeshtasticMessageReceivedAsync;
        public async Task StartAsync(IServiceScope scope, CancellationToken ct = default)
        {
            if (_options.MapMqttServers == null || _options.MapMqttServers.Length == 0)
            {
                logger.LogInformation("MapMqttService: no servers configured, skipping.");
                return;
            }

            await FillNetworks(scope);

            _connectTasks.Clear();

            foreach (var server in _options.MapMqttServers.Where(x => x.AnalyticsDownlinkEnabled || x.UplinkMode != UplinkMode.Disabled))
            {
                var t = Task.Run(async () => await ConnectServerAsync(server, ct), ct);
                _connectTasks.Add(t);
            }
        }

        public async Task FillNetworks(IServiceScope scope)
        {
            var regService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
            var networks = await regService.GetNetworksCached();
            var publicChannels = await regService.GetAllPublicChannelsAsync();
            _networks = networks.Select(n =>
            {
                var publicChannelsForNetwork = publicChannels.Where(c => c.NetworkId == n.Id).Select(c => c.Name).ToArray();
                return new NetworkShortInfo
                {
                    Id = n.Id,
                    SaveAnalytics = n.SaveAnalytics,
                    ShortName = n.ShortName,
                    PublicChannelNames = publicChannelsForNetwork
                };
            }).ToDictionary(x => x.Id);

            _networkByShortName = _networks
                .Where(x => !string.IsNullOrEmpty(x.Value.ShortName))
                .ToLookup(x => x.Value.ShortName, x => x.Key);
        }

        public bool UplinkEnabled => _clients?.Any(x => x.server.UplinkMode != UplinkMode.Disabled) == true;


        public bool ShouldUplink(OkToMqttStatus okToMqttStatus, int networkId, UplinkPrivacyMode privacyMode)
        {
            foreach (var (_, server) in _clients.Where(x => 
                    x.server.UplinkMode != UplinkMode.Disabled
                    && (x.server.FilterByNetworkId == null || x.server.FilterByNetworkId == networkId)))
            {
                if (ShouldUplink(okToMqttStatus, server.UplinkMode, privacyMode))
                {
                    return true;
                }
            }
            return false;
        }

        public async ValueTask<bool> PublishMeshtasticMessage(
          int networkId,
          UplinkPrivacyMode privacyMode,
          OkToMqttStatus okToMqttStatus,
          ServiceEnvelope envelope)
        {
            bool published = false;
            foreach (var (client, server) in _clients.Where(x => 
                x.server.UplinkMode != UplinkMode.Disabled 
                && x.mqttClient.IsConnected
                && (x.server.FilterByNetworkId == null || x.server.FilterByNetworkId == networkId)))
            {
                var shouldUplink = ShouldUplink(okToMqttStatus, server.UplinkMode, privacyMode);
                if (shouldUplink)
                {
                    await PublishToClientAsync(client, server, networkId, envelope);
                    published = true;
                }
            }
            return published;
        }

        private static bool ShouldUplink(
            OkToMqttStatus okToMqttStatus,
            UplinkMode uplinkMode,
            UplinkPrivacyMode privacyMode)
        {
            switch (privacyMode)
            {
                case UplinkPrivacyMode.Default:
                    return ShouldUplinkNormalPrivacy(okToMqttStatus, uplinkMode);
                case UplinkPrivacyMode.OnlyOkToMqttAndPKI:
                    return ShouldUplinkNormalPrivacy(okToMqttStatus, uplinkMode)
                        && (okToMqttStatus == OkToMqttStatus.True
                        || okToMqttStatus == OkToMqttStatus.UnknownPkiEncrypted);
                default:
                    throw new NotImplementedException($"UplinkPrivacyMode {privacyMode} not implemented");
            }
        }

        private static bool ShouldUplinkNormalPrivacy(OkToMqttStatus okToMqttStatus, UplinkMode uplinkMode)
           => uplinkMode switch
           {
               UplinkMode.Disabled => false,
               UplinkMode.MqttOkExplicitTrueOnly => okToMqttStatus == OkToMqttStatus.True,
               UplinkMode.MqttOkTrueAndUnknown => okToMqttStatus == OkToMqttStatus.True 
                    || okToMqttStatus == OkToMqttStatus.Unknown
                    || okToMqttStatus == OkToMqttStatus.UnknownPkiEncrypted,
               UplinkMode.MqttOkTrueAndUnknownAndFalseExceptPosition =>
                   okToMqttStatus == OkToMqttStatus.True
                        || okToMqttStatus == OkToMqttStatus.Unknown
                        || okToMqttStatus == OkToMqttStatus.UnknownPkiEncrypted
                        || okToMqttStatus == OkToMqttStatus.False_NotPosition,
               UplinkMode.All => true,
               UplinkMode.MqttNotOkOnly => okToMqttStatus == OkToMqttStatus.False_NotPosition
                   || okToMqttStatus == OkToMqttStatus.False_IsPosition,
               UplinkMode.MqttNotOkAndUnknown => okToMqttStatus == OkToMqttStatus.False_NotPosition
                   || okToMqttStatus == OkToMqttStatus.False_IsPosition
                   || okToMqttStatus == OkToMqttStatus.Unknown
                   || okToMqttStatus == OkToMqttStatus.UnknownPkiEncrypted,
               UplinkMode.MqttNotOkAndUnknownExceptPosition => okToMqttStatus == OkToMqttStatus.False_NotPosition
                   || okToMqttStatus == OkToMqttStatus.Unknown
                   || okToMqttStatus == OkToMqttStatus.UnknownPkiEncrypted,
               UplinkMode.MqttNotOkOnlyExceptPosition => okToMqttStatus == OkToMqttStatus.False_NotPosition,
               _ => throw new NotImplementedException($"UplinkMode {uplinkMode} not implemented")
           };


        private async Task PublishToClientAsync(IMqttClient client, MapMqttServerOptions server, int networkId, ServiceEnvelope envelope)
        {
            if (server.UplinkMode == UplinkMode.Disabled)
            {
                throw new InvalidOperationException($"Server {server.Address} does not have uplink enabled.");
            }
            try
            {
                string topic = null;
                if (envelope.Packet.PayloadVariantCase == MeshPacket.PayloadVariantOneofCase.Encrypted
                    && !string.IsNullOrEmpty(server.EncryptedTopicPrefix))
                {
                    topic = string.Concat(server.EncryptedTopicPrefix.TrimEnd('/'), '/', envelope.ChannelId, '/' + envelope.GatewayId);
                }
                else if (envelope.Packet.PayloadVariantCase == MeshPacket.PayloadVariantOneofCase.Decoded
                    && envelope.Packet.Decoded?.Portnum == PortNum.MapReportApp
                    && !string.IsNullOrEmpty(server.MapTopic))
                {
                    topic = string.Concat(server.MapTopic);
                }

                if (topic == null)
                {
                    return;
                }

                if (topic.Contains(NetworkShortNameToken))
                {
                    var network = _networks.GetValueOrDefault(networkId);
                    if (network.ShortName != null)
                    {
                        topic = topic.Replace(NetworkShortNameToken, network.ShortName);
                    }
                }

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(envelope.ToByteArray())
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await client.PublishAsync(message);
                logger.LogDebug("Published map MQTT message to {topic}", topic);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error publishing map MQTT message to {Server}", server.Address);
            }
        }
        private async Task ConnectServerAsync(MapMqttServerOptions server, CancellationToken ct)
        {
            try
            {
                var client = mqttClientFactory.CreateMqttClient();
                lock (_clients) { _clients.Add((client, server)); }

                client.ApplicationMessageReceivedAsync += args =>
                    HandleMessageAsync(args, server);

                client.DisconnectedAsync += async e =>
                {
                    if (_connectionCts == null
                        || _connectionCts.IsCancellationRequested)
                    {
                        return;
                    }
                    logger.LogWarning("MapMqtt [{Server}] disconnected: {Reason}", server.Address, e.Reason);
                    await Task.Delay(ReconnectMillisecondsDelay, _connectionCts.Token);
                    await ConnectClientAsync(client, server, _connectionCts.Token);
                };

                await ConnectClientAsync(client, server, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MapMqtt [{Server}] connection error", server.Address);
            }
        }

        private async Task<bool> ConnectClientAsync(IMqttClient client, MapMqttServerOptions server, CancellationToken ct)
        {
            if (client.IsConnected)
                return true;

            try
            {
                var sslOptions = new MqttClientTlsOptions
                {
                    UseTls = server.UseTls,
                    AllowUntrustedCertificates = server.AllowUntrustedCertificates,
                    IgnoreCertificateChainErrors = server.AllowUntrustedCertificates,
                    IgnoreCertificateRevocationErrors = server.AllowUntrustedCertificates,
                };

                if (server.AllowUntrustedCertificates)
                {
                    sslOptions.CertificateValidationHandler = _ => true;
                }

                var clientId = $"{ClientId}_{(server.UplinkMode != UplinkMode.Disabled ? $"u{(int)server.UplinkMode}" : "")}{(server.AnalyticsDownlinkEnabled ? "d" : "")}";

                var builder = new MqttClientOptionsBuilder()
                    .WithTcpServer(server.Address, server.Port)
                    .WithTlsOptions(sslOptions)
                    .WithClientId(clientId);

                if (!string.IsNullOrWhiteSpace(server.User))
                    builder = builder.WithCredentials(server.User, server.Password);

                logger.LogInformation("MapMqtt [{Server}] connecting...", server.Address);

                using var timeoutCts = new CancellationTokenSource(ConnectMillisecondsTtl);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                var result = await client.ConnectAsync(builder.Build(), linkedCts.Token);
                if (result.ResultCode != MqttClientConnectResultCode.Success)
                {
                    logger.LogError("MapMqtt [{Server}] failed to connect: {Code}", server.Address, result.ResultCode);
                    return false;
                }
                logger.LogInformation("MapMqtt [{Server}] connected, topic prefix: {Topic}", server.Address, server.EncryptedTopicPrefix);

                if (server.AnalyticsDownlinkEnabled)
                {
                    var topicFilters = new List<MqttTopicFilter>();
                    var hasNetworkNameToken = server.EncryptedTopicPrefix.Contains(NetworkShortNameToken);
                    if (hasNetworkNameToken && (_networks == null || _networks.Count == 0))
                    {
                        logger.LogWarning("MapMqtt [{Server}] has {Token} in topic prefix but no networks found, skipping subscription.",
                            server.Address, NetworkShortNameToken);
                        return true; // not a failure condition, just means we won't receive messages until networks are available
                    }
                    else if (hasNetworkNameToken)
                    {
                        foreach (var network in _networks.Values.Where(x => !string.IsNullOrEmpty(x.ShortName) && x.SaveAnalytics).Distinct())
                        {
                            foreach (var channelName in network.PublicChannelNames.Distinct())
                            {
                                var topic = server.EncryptedTopicPrefix.Replace(NetworkShortNameToken, network.ShortName).TrimEnd('/') + '/' + channelName + "/#";

                                topicFilters.Add(new MqttTopicFilter
                                {
                                    Topic = topic,
                                    QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce
                                });
                            }
                        }
                    }
                    else
                    {
                        foreach (var network in _networks.Values.Where(x => x.SaveAnalytics).Distinct())
                        {
                            foreach (var channelName in network.PublicChannelNames.Distinct())
                            {
                                var topic = server.EncryptedTopicPrefix.TrimEnd('/') + '/' + channelName + "/#";

                                topicFilters.Add(new MqttTopicFilter
                                {
                                    Topic = topic,
                                    QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
                                });
                            }
                        }
                    }

                    await client.SubscribeAsync(new MqttClientSubscribeOptions
                    {
                        TopicFilters = topicFilters
                    }, ct);
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                throw; // shutting down
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MapMqtt [{Server}] connect/subscribe error", server.Address);
                return false;
            }
        }

        private async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs args, MapMqttServerOptions server)
        {
            try
            {
                var payload = args.ApplicationMessage.Payload;
                if (payload.Length == 0)
                    return;

                int? networkId = GetNetworkIdFromTopicWithSaveAnalyticsEnabled(args.ApplicationMessage.Topic, server);

                if (networkId.HasValue)
                {
                    var network = _networks.GetValueOrDefault(networkId.Value);
                    if (!network.SaveAnalytics)
                    {
                        return;
                    }
                }

                ServiceEnvelope env;
                try
                {
                    env = ServiceEnvelope.Parser.ParseFrom(payload);
                }
                catch
                {
                    return; // not a valid ServiceEnvelope
                }
                await MeshtasticMessageReceivedAsync?.Invoke(new DataEventArgs<NetworkServiceEnvelope>(new NetworkServiceEnvelope
                {
                    NetworkId = networkId,
                    Envelope = env
                }));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MapMqtt: error handling packet");
            }
        }

        private int? GetNetworkIdFromTopicWithSaveAnalyticsEnabled(string topic, MapMqttServerOptions server)
        {
            var networkNameTokenIndex = server.EncryptedTopicPrefix.IndexOf(NetworkShortNameToken);
            if (networkNameTokenIndex < 0)
                return server.DefaultNetworkId;


            var nextSlashIndex = topic.IndexOf('/', networkNameTokenIndex);
            if (nextSlashIndex < 0)
                return server.DefaultNetworkId;


            var networkShortName = topic[networkNameTokenIndex..nextSlashIndex];
            if (string.IsNullOrEmpty(networkShortName))
            {
                return server.DefaultNetworkId;
            }
            var networkIds = _networkByShortName[networkShortName];
            foreach (var id in networkIds)
            {
                var network = _networks.GetValueOrDefault(id);
                if (network.SaveAnalytics)
                {
                    return id;
                }
            }
            return server.DefaultNetworkId;
        }

        public async ValueTask DisposeAsync()
        {
            if (_connectionCts == null || _connectionCts.IsCancellationRequested)
            {
                return;
            }

            _connectionCts.Cancel();
            List<IMqttClient> snapshot;
            lock (_clients) { snapshot = [.. _clients.Select(x => x.mqttClient)]; }
            foreach (var c in snapshot)
            {
                try
                {
                    if (c.IsConnected)
                        await c.DisconnectAsync(MqttClientDisconnectOptionsReason.AdministrativeAction);
                    c.Dispose();
                }
                catch { /* best effort */ }
            }
            _connectionCts.Dispose();
            _connectionCts = null;
            GC.SuppressFinalize(this);
        }
    }

    public sealed class MapPacketEventArgs(ServiceEnvelope envelope, long mapGatewayId)
    {
        public ServiceEnvelope Envelope { get; } = envelope;
        /// <summary>The map gateway node ID (the node that heard the packet over radio).</summary>
        public long MapGatewayId { get; } = mapGatewayId;
    }
}
