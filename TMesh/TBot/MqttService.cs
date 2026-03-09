using Google.Protobuf;
using Meshtastic.Protobufs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Packets;
using Shared.Models;
using System.Text.Json;
using TBot.Models;

namespace TBot
{
    public class MqttService : IAsyncDisposable
    {
        private const int ReconnectMillisecondsDelay = 5000;
#if DEBUG
        const string ClientId = "TBotDebug";
#else
        const string ClientId = "TBot";
#endif

        public MqttService(
            ILogger<MqttService> logger,
            IOptions<TBotOptions> options,
            MqttClientFactory mqttClientFactory)
        {
            _logger = logger;
            _options = options.Value;
            _mqttClientFactory = mqttClientFactory;
            SetMeshtasticTopic();
        }

        private readonly ILogger<MqttService> _logger;
        private readonly CancellationTokenSource _connectionCts = new();
        private readonly TBotOptions _options;
        private readonly MqttClientFactory _mqttClientFactory;
        private IMqttClient _client;
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private string _ourGatewayMeshtasicTopic;
        private string _directGatewayPrefix;

        public event Func<DataEventArgs<string>, Task> TelegramMessageReceivedAsync;
        public event Func<DataEventArgs<ServiceEnvelope>, Task> MeshtasticMessageReceivedAsync;
        public event Func<DataEventArgs<long>, Task> MessageSent;

        private void SetMeshtasticTopic()
        {
            _ourGatewayMeshtasicTopic = string.Concat(
                _options.MqttMeshtasticTopicPrefix.TrimEnd('/'),
                "/PKI/",
                MeshtasticService.GetMeshtasticNodeHexId(_options.MeshtasticNodeId));

            _directGatewayPrefix = string.Concat(
                _options.MqttMeshtasticTopicPrefix.TrimEnd('/'),
                "/PKI/");
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            while (!await EnsureMqttConnectedAsync(ct))
            {
                _logger.LogWarning("MQTT connection failed, retrying in 5 seconds...");
                await Task.Delay(ReconnectMillisecondsDelay, ct);
            }
        }
        private async Task<bool> EnsureMqttConnectedAsync(CancellationToken ct)
        {
            if (_client?.IsConnected == true) return true;
            await _connectLock.WaitAsync(ct);
            try
            {
                if (_client?.IsConnected == true) return true;
                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }

                _client = _mqttClientFactory.CreateMqttClient();
                _client.ApplicationMessageReceivedAsync += HandleMqttMessageAsync;
                _client.DisconnectedAsync += async e =>
                {
                    if (e.Reason == MqttClientDisconnectReason.AdministrativeAction)
                    {
                        return; // don't attempt reconnect if we intentionally disconnected
                    }

                    try
                    {
                        _logger.LogWarning("MQTT disconnected: {Reason}", e.Reason);
                        await Task.Delay(ReconnectMillisecondsDelay, ct);
                        await ConnectAsync(_connectionCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // shutting down
                    }
                };

                var sslOptions = new MqttClientTlsOptions
                {
                    IgnoreCertificateChainErrors = _options.MqttAllowUntrustedCertificates,
                    IgnoreCertificateRevocationErrors = _options.MqttAllowUntrustedCertificates,
                    AllowUntrustedCertificates = _options.MqttAllowUntrustedCertificates,
                    UseTls = _options.MqttUseTls
                };

                if (_options.MqttAllowUntrustedCertificates)
                {
                    sslOptions.CertificateValidationHandler = context =>
                    {
                        // Additional custom validation can be added here if needed
                        return true; // accept all for now if untrusted allowed
                    };
                }

                var builder = new MqttClientOptionsBuilder()
                    .WithTcpServer(_options.MqttAddress, _options.MqttPort)
                    .WithCredentials(_options.MqttUser, _options.MqttPassword)
                    .WithTlsOptions(sslOptions)
                    .WithClientId(ClientId)
                    .WithSessionExpiryInterval(30 * 24 * 3600)
                    .WithCleanSession(false);

                var options = builder.Build();
                var result = await _client.ConnectAsync(options, ct);
                if (result.ResultCode != MqttClientConnectResultCode.Success)
                {
                    _logger.LogError("Failed to connect MQTT: {Code}", result.ResultCode);
                    return false;
                }
                _logger.LogInformation("MQTT connected to {Host}:{Port}", _options.MqttAddress, _options.MqttPort);

                var filters = new List<MqttTopicFilter>()
                {
                     new() {
                        Topic = _options.MqttTelegramTopic,
                        QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
                    },
                    new() {
                        NoLocal = true,
                        Topic = _options.MqttMeshtasticTopicPrefix.TrimEnd('/') + "/PKI/#",
                        QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
                    },
                    new() {
                        NoLocal = true,
                        Topic = _options.MqttMeshtasticTopicPrefix.TrimEnd('/') + "/UCH/#",
                        QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
                    },
                    new() {
                        NoLocal = true,
                        Topic = _options.MqttMeshtasticTopicPrefix.TrimEnd('/') +'/' + _options.MeshtasticPrimaryChannelName + "/#",
                        QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
                    }
                };

                if (_options.MeshtasticSecondayChannels != null)
                {
                    foreach (var item in _options.MeshtasticSecondayChannels)
                    {
                        filters.Add(new MqttTopicFilter
                        {
                            NoLocal = true,
                            Topic = _options.MqttMeshtasticTopicPrefix.TrimEnd('/') + '/' + item.Name + "/#",
                            QualityOfServiceLevel = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce
                        });
                    }
                }

                await _client.SubscribeAsync(
                    new MqttClientSubscribeOptions
                    {
                        TopicFilters = filters
                    }, ct);

                _logger.LogInformation("Subscribed to mqtt topics");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting MQTT");
                return false;
            }
            finally
            {
                _connectLock.Release();
            }

        }



        public async Task PublishMeshtasticMessage(
            ServiceEnvelope envelope,
            long? relayThroughGatewayId)
        {
            var topic = relayThroughGatewayId == null
                ? _ourGatewayMeshtasicTopic
                : string.Concat(_directGatewayPrefix,
                    MeshtasticService.GetMeshtasticNodeHexId(relayThroughGatewayId.Value));

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(envelope.ToByteArray())
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await EnsureMqttConnectedAsync(_connectionCts.Token);
            await _client.PublishAsync(message);
            if (MessageSent != null)
            {
                await MessageSent.Invoke(new DataEventArgs<long>(envelope.Packet.Id));
            }
            _logger.LogInformation("Published MQTT message to {topic}", topic);
        }

        public async Task PublishStatus(BotStats stats)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(_options.MqttStatusTopic)
                .WithPayload(JsonSerializer.Serialize(stats))
                .WithRetainFlag(true)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await EnsureMqttConnectedAsync(_connectionCts.Token);
            await _client.PublishAsync(message);
            _logger.LogInformation("Published MQTT status message to {topic}", _options.MqttStatusTopic);
        }

        private async Task HandleMqttMessageAsync(MqttApplicationMessageReceivedEventArgs arg)
        {
            try
            {
                var topic = arg.ApplicationMessage.Topic;
                _logger.LogInformation("Received MQTT message on {Topic}", topic);
                if (topic == _options.MqttTelegramTopic)
                {
                    var payload = arg.ApplicationMessage.ConvertPayloadToString();
                    await TelegramMessageReceivedAsync?.Invoke(new DataEventArgs<string>(payload));
                }
                else if (topic.StartsWith(_options.MqttMeshtasticTopicPrefix))
                {
                    if (topic == _ourGatewayMeshtasicTopic)
                    {
                        _logger.LogDebug("Ignoring Meshtastic message sent by ourselves");
                        return;
                    }

                    var payload = arg.ApplicationMessage.Payload;
                    ServiceEnvelope env;
                    try
                    {
                        env = ServiceEnvelope.Parser.ParseFrom(payload);
                    }
                    catch
                    {
                        return; // not a valid ServiceEnvelope
                    }
                    if (env.GatewayId == MeshtasticService.GetMeshtasticNodeHexId(_options.MeshtasticNodeId))
                    {
                        //This can happen when we sent a message using direct gateway topic
                        _logger.LogDebug("Ignoring Meshtastic message sent by ourselves");
                        return;
                    }

                    await MeshtasticMessageReceivedAsync?.Invoke(new DataEventArgs<ServiceEnvelope>(env));
                }
                else
                {
                    _logger.LogWarning("Received MQTT message on unknown topic: {Topic}", topic);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling MQTT message");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _connectionCts.Cancel();
            if (_client != null)
            {
                await _client.DisconnectAsync(MqttClientDisconnectOptionsReason.AdministrativeAction);
                _client.Dispose();
                _client = null;
            }
            _connectionCts.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
