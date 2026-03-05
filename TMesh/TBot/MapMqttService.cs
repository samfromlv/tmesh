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

        private readonly ILogger<MapMqttService> _logger;
        private readonly TBotOptions _options;
        private readonly MqttClientFactory _mqttClientFactory;

        private readonly List<IMqttClient> _clients = [];

        /// <summary>Raised when a PKI-encrypted telemetry packet from a TMesh gateway is received.</summary>
        public event Func<DataEventArgs<ServiceEnvelope>, Task> MeshtasticMessageReceivedAsync;

        public async Task StartAsync(CancellationToken ct = default)
        {
            if (_options.MapMqttServers == null || _options.MapMqttServers.Length == 0)
            {
                _logger.LogInformation("MapMqttService: no servers configured, skipping.");
                return;
            }

            foreach (var server in _options.MapMqttServers)
            {
                await ConnectServerAsync(server, ct);
            }
        }

        private async Task ConnectServerAsync(MapMqttServerOptions server, CancellationToken ct)
        {
            try
            {
                var client = _mqttClientFactory.CreateMqttClient();
                lock (_clients) { _clients.Add(client); }

                client.ApplicationMessageReceivedAsync += args =>
                    HandleMessageAsync(args, server);

                client.DisconnectedAsync += async e =>
                {
                    _logger.LogWarning("MapMqtt [{Server}] disconnected: {Reason}", server.Address, e.Reason);
                    await Task.Delay(5000, CancellationToken.None);
                    await ConnectClientAsync(client, server, CancellationToken.None);
                };

                await ConnectClientAsync(client, server, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MapMqtt [{Server}] connection error", server.Address);
            }
        }

        private async Task ConnectClientAsync(IMqttClient client, MapMqttServerOptions server, CancellationToken ct)
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

                var builder = new MqttClientOptionsBuilder()
                    .WithTcpServer(server.Address, server.Port)
                    .WithTlsOptions(sslOptions)
                    .WithClientId(ClientId);

                if (!string.IsNullOrWhiteSpace(server.User))
                    builder = builder.WithCredentials(server.User, server.Password);

                var result = await client.ConnectAsync(builder.Build(), ct);
                if (result.ResultCode != MqttClientConnectResultCode.Success)
                {
                    _logger.LogError("MapMqtt [{Server}] failed to connect: {Code}", server.Address, result.ResultCode);
                    return;
                }
                _logger.LogInformation("MapMqtt [{Server}] connected, topic prefix: {Topic}", server.Address, server.TopicPrefix);

                // Subscribe to PKI channel only – that's where encrypted device telemetry lives
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "MapMqtt [{Server}] connect/subscribe error", server.Address);
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
            List<IMqttClient> snapshot;
            lock (_clients) { snapshot = [.. _clients]; }
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
