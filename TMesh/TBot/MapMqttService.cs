using Google.Protobuf;
using Meshtastic.Protobufs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Packets;
using Shared.Models;
using System.Text;
using TBot.Models;

namespace TBot
{
    public class MapMqttService(
        ILogger<MapMqttService> logger,
        IOptions<TBotOptions> options,
        MqttClientFactory mqttClientFactory) : IAsyncDisposable
    {
        private const int ReconnectMillisecondsDelay = 10000;
#if DEBUG
        const string ClientId = "TMeshDebug";
#else
        const string ClientId = "TMesh";
#endif

        const string NetworkShortNameToken = "{{NetworkShortName}}";
        private CancellationTokenSource _connectionCts = new();
        private readonly TBotOptions _options = options.Value;
        private Dictionary<int, (string NetworkShortName, string ChannelName, bool SaveAnalytics)> _networks;
        private ILookup<string, int> _networkByShortName;

        private readonly List<(IMqttClient mqttClient, MapMqttServerOptions server)> _clients = [];

        public ServerStatus[] GetStatus()
        {
            List<ServerStatus> status = [];

            (IMqttClient mqttClient, MapMqttServerOptions server)[] snapshot;
            lock (_clients)
            {
                snapshot =_clients.ToArray();
            }

            return snapshot.Select(x =>
            {
                var s = x.mqttClient.IsConnected ? "Connected" : "Disconnected";
                var serverId = new StringBuilder();
                if (x.server.AnalyticsDownlinkEnabled)
                {
                    serverId.Append('d');
                }
                if (x.server.UplinkEnabled)
                {
                    serverId.Append('u');
                }
                serverId.Append('-');
                serverId.Append(x.server.Address);
                return new ServerStatus
                {
                    ServerID = serverId.ToString(),
                    Status = s
                };
            }).ToArray();
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

            foreach (var server in _options.MapMqttServers.Where(x => x.AnalyticsDownlinkEnabled || x.UplinkEnabled))
            {
                await ConnectServerAsync(server, ct);
            }
        }

        public async Task FillNetworks(IServiceScope scope)
        {
            var regService = scope.ServiceProvider.GetRequiredService<RegistrationService>();
            var networks = await regService.GetNetworksCached();
            var primaryChannels = await regService.GetAllPrimaryChannelsCached();
            _networks = networks.Select(n =>
            {
                var primaryChannel = primaryChannels.GetValueOrDefault(n.Id);
                return new
                {
                    n.Id,
                    NetworkShortName = n.ShortName,
                    n.SaveAnalytics,
                    ChannelName = primaryChannel?.Name ?? $"net{n.Id}"
                };
            }).ToDictionary(x => x.Id, x => (x.NetworkShortName, x.ChannelName, x.SaveAnalytics));

            _networkByShortName = _networks
                .Where(x => !string.IsNullOrEmpty(x.Value.NetworkShortName))
                .ToLookup(x => x.Value.NetworkShortName, x => x.Key);
        }

        public bool UplinkEnabled => _clients?.Any(x => x.server.UplinkEnabled) == true;

        public async ValueTask PublishMeshtasticMessage(
          int networkId,
          ServiceEnvelope envelope)
        {
            foreach (var (client, server) in _clients.Where(x => x.server.UplinkEnabled && x.mqttClient.IsConnected))
            {
                await PublishToClientAsync(client, server, networkId, envelope);
            }
        }

        private async Task PublishToClientAsync(IMqttClient client, MapMqttServerOptions server, int networkId, ServiceEnvelope envelope)
        {
            if (!server.UplinkEnabled)
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
                    if (network.NetworkShortName != null)
                    {
                        topic = topic.Replace(NetworkShortNameToken, network.NetworkShortName);
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
                    await ForceConnectClientAsync(client, server, _connectionCts.Token);
                };

                await ForceConnectClientAsync(client, server, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MapMqtt [{Server}] connection error", server.Address);
            }
        }


        private async Task ForceConnectClientAsync(IMqttClient client, MapMqttServerOptions server, CancellationToken ct)
        {
            bool connected = false;
            while (!connected)
            {
                try
                {
                    connected = await ConnectClientAsync(client, server, ct);
                }
                catch (OperationCanceledException)
                {
                    return; // shutting down
                }
                if (!connected)
                {
                    logger.LogInformation("Retrying MapMqtt [{Server}] connection...", server.Address);
                    await Task.Delay(ReconnectMillisecondsDelay, ct);
                }
            }
        }

        private async Task<bool> ConnectClientAsync(IMqttClient client, MapMqttServerOptions server, CancellationToken ct)
        {
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

                var clientId = $"{ClientId}_{(server.UplinkEnabled ? "u" : "")}{(server.AnalyticsDownlinkEnabled ? "d" : "")}";

                var builder = new MqttClientOptionsBuilder()
                    .WithTcpServer(server.Address, server.Port)
                    .WithTlsOptions(sslOptions)
                    .WithClientId(clientId);

                if (!string.IsNullOrWhiteSpace(server.User))
                    builder = builder.WithCredentials(server.User, server.Password);

                var result = await client.ConnectAsync(builder.Build(), ct);
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
                        foreach (var (networkShortName, channelName, _) in _networks.Values.Where(x => !string.IsNullOrEmpty(x.NetworkShortName) && x.SaveAnalytics).Distinct())
                        {
                            var topic = server.EncryptedTopicPrefix.Replace(NetworkShortNameToken, networkShortName).TrimEnd('/') + '/' + channelName + "/#";

                            topicFilters.Add(new MqttTopicFilter
                            {
                                Topic = topic,
                                QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
                            });
                        }
                    }
                    else
                    {
                        foreach (var channelName in _networks.Values.Where(x => x.SaveAnalytics).Select(x => x.ChannelName).Distinct())
                        {
                            var topic = server.EncryptedTopicPrefix.TrimEnd('/') + '/' + channelName + "/#";

                            topicFilters.Add(new MqttTopicFilter
                            {
                                Topic = topic,
                                QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
                            });
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
