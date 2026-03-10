using Google.Protobuf;
using Meshtastic.Protobufs;
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

        public MapMqttService(
            ILogger<MapMqttService> logger,
            IOptions<TBotOptions> options,
            MqttClientFactory mqttClientFactory)
        {
            _logger = logger;
            _options = options.Value;
            _mqttClientFactory = mqttClientFactory;
        }

        private readonly CancellationTokenSource _connectionCts = new();

        private readonly ILogger<MapMqttService> _logger;
        private readonly TBotOptions _options;
        private readonly MqttClientFactory _mqttClientFactory;

        private readonly List<(IMqttClient mqttClient, MapMqttServerOptions server)> _clients = new();

        /// <summary>Raised when a PKI-encrypted telemetry packet from a TMesh gateway is received.</summary>
        public event Func<DataEventArgs<ServiceEnvelope>, Task> MeshtasticMessageReceivedAsync;

        public async Task StartAsync(CancellationToken ct = default)
        {
            if (_options.MapMqttServers == null || _options.MapMqttServers.Length == 0)
            {
                _logger.LogInformation("MapMqttService: no servers configured, skipping.");
                return;
            }

            foreach (var server in _options.MapMqttServers.Where(x => x.AnalyticsDownlinkEnabled || x.UplinkEnabled))
            {
                await ConnectServerAsync(server, ct);
            }
        }


        public bool UplinkEnabled => _clients?.Any(x => x.server.UplinkEnabled) == true;

        public async ValueTask PublishMeshtasticMessage(
          ServiceEnvelope envelope)
        {
            foreach (var (client, server) in _clients.Where(x => x.server.UplinkEnabled && x.mqttClient.IsConnected))
            {
                await PublishToClientAsync(client, server, envelope);
            }
        }

        private async Task PublishToClientAsync(IMqttClient client, MapMqttServerOptions server, ServiceEnvelope envelope)
        {
            try
            {
                var topic = string.Concat(server.TopicPrefix.TrimEnd('/'), '/', envelope.ChannelId, '/' + envelope.GatewayId);
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
                    if (e.Reason == MqttClientDisconnectReason.AdministrativeAction)
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
                _logger.LogInformation("MapMqtt [{Server}] connected, topic prefix: {Topic}", server.Address, server.TopicPrefix);

                if (server.AnalyticsDownlinkEnabled)
                {
                    var topic = server.TopicPrefix.TrimEnd('/') + '/' + _options.MeshtasticPrimaryChannelName + "/#";
                    await client.SubscribeAsync(new MqttClientSubscribeOptions
                    {
                        TopicFilters = [new MqttTopicFilter
                        {
                            Topic = topic,
                            QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
                        }]
                    }, ct);
                    _logger.LogInformation("MapMqtt [{Server}] subscribed to {Topic}", server.Address, topic);
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
