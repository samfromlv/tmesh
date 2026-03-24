using Google.Protobuf;
using Meshtastic.Protobufs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Packets;
using Shared.Models;
using TBot.Models;

namespace TBot
{
    public class MapMqttService : IAsyncDisposable
    {
        private const int ReconnectMillisecondsDelay = 10000;
#if DEBUG
        const string ClientId = "TMeshDebug";
#else
        const string ClientId = "TMesh";
#endif

        const string NetworkShortNameToken = "{{NetworkShortName}}";

        public MapMqttService(
            ILogger<MapMqttService> logger,
            IOptions<TBotOptions> options,
            MqttClientFactory mqttClientFactory,
            IServiceProvider services)
        {
            _logger = logger;
            _options = options.Value;
            _mqttClientFactory = mqttClientFactory;
            _services = services;
        }

        private readonly CancellationTokenSource _connectionCts = new();

        private readonly ILogger<MapMqttService> _logger;
        private readonly TBotOptions _options;
        private readonly MqttClientFactory _mqttClientFactory;
        private readonly IServiceProvider _services;

        private Dictionary<int, (string NetworkShortName, string ChannelName)> _networks;

        private readonly List<(IMqttClient mqttClient, MapMqttServerOptions server)> _clients = new();

        /// <summary>Raised when a PKI-encrypted telemetry packet from a TMesh gateway is received.</summary>
        public event Func<DataEventArgs<ServiceEnvelope>, Task> MeshtasticMessageReceivedAsync;
        public async Task StartAsync(IServiceScope scope, CancellationToken ct = default)
        {
            if (_options.MapMqttServers == null || _options.MapMqttServers.Length == 0)
            {
                _logger.LogInformation("MapMqttService: no servers configured, skipping.");
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
                    ChannelName = primaryChannel?.Name ?? $"net{n.Id}"
                };
            }).ToDictionary(x => x.Id, x => (x.NetworkShortName, x.ChannelName));
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
                _logger.LogInformation("Published map MQTT message to {topic}", topic);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing map MQTT message to {Server}", server.Address);
            }
        }
        private async Task ConnectServerAsync(MapMqttServerOptions server, CancellationToken ct)
        {
            try
            {
                var client = _mqttClientFactory.CreateMqttClient();
                lock (_clients) { _clients.Add((client, server)); }

                client.ApplicationMessageReceivedAsync += args =>
                    HandleMessageAsync(args, server);

                client.DisconnectedAsync += async e =>
                {
                    if (_connectionCts.IsCancellationRequested)
                    {
                        return;
                    }

                    _logger.LogWarning("MapMqtt [{Server}] disconnected: {Reason}", server.Address, e.Reason);
                    await Task.Delay(ReconnectMillisecondsDelay, _connectionCts.Token);
                    try
                    {
                        await ForceConnectClientAsync(client, server, _connectionCts.Token);
                    }
                    catch (OperationCanceledException)
                    { /* shutting down */
                    }
                };

                await ForceConnectClientAsync(client, server, _connectionCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MapMqtt [{Server}] connection error", server.Address);
            }
        }


        private async Task ForceConnectClientAsync(IMqttClient client, MapMqttServerOptions server, CancellationToken ct)
        {
            while (!await ConnectClientAsync(client, server, ct))
            {
                _logger.LogInformation("Retrying MapMqtt [{Server}] connection in 5 seconds...", server.Address);
                await Task.Delay(ReconnectMillisecondsDelay, ct);
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
                    _logger.LogError("MapMqtt [{Server}] failed to connect: {Code}", server.Address, result.ResultCode);
                    return false;
                }
                _logger.LogInformation("MapMqtt [{Server}] connected, topic prefix: {Topic}", server.Address, server.EncryptedTopicPrefix);

                if (server.AnalyticsDownlinkEnabled)
                {
                    var topicFilters = new List<MqttTopicFilter>();
                    var hasNetworkNameToken = server.EncryptedTopicPrefix.Contains(NetworkShortNameToken);
                    if (hasNetworkNameToken && (_networks == null || _networks.Count == 0))
                    {
                        _logger.LogWarning("MapMqtt [{Server}] has {Token} in topic prefix but no networks found, skipping subscription.",
                            server.Address, NetworkShortNameToken);
                        return true; // not a failure condition, just means we won't receive messages until networks are available
                    }
                    else if (hasNetworkNameToken)
                    {
                        foreach (var (networkShortName, channelName) in _networks.Values.Where(x => !string.IsNullOrEmpty(x.NetworkShortName)))
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
                        foreach (var channelName in _networks.Values.Select(x => x.ChannelName).Distinct())
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "MapMqtt [{Server}] connect/subscribe error", server.Address);
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

                ServiceEnvelope env;
                try
                {
                    env = ServiceEnvelope.Parser.ParseFrom(payload);
                }
                catch
                {
                    return; // not a valid ServiceEnvelope
                }
                await MeshtasticMessageReceivedAsync?.Invoke(new DataEventArgs<ServiceEnvelope>(env));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MapMqtt: error handling packet");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _connectionCts.Cancel();
            List<IMqttClient> snapshot;
            lock (_clients) { snapshot = _clients.Select(x => x.Item1).ToList(); }
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
