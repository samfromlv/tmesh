using Meshtastic.Protobufs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Models;

namespace TBot
{
    public class MqttService : IAsyncDisposable
    {
        public MqttService(
            ILogger<MqttService> logger,
            IOptions<TBotOptions> options)
        {
            _logger = logger;
            _options = options.Value;
        }

        private readonly ILogger<MqttService> _logger;
        private readonly MqttClientFactory _factory = new();
        private readonly TBotOptions _options;
        private IMqttClient _client;
        private readonly SemaphoreSlim _connectLock = new(1, 1);

        public event Func<DataEventArgs<string>, Task> TelegramMessageReceivedAsync;
        public event Func<DataEventArgs<ServiceEnvelope>, Task> MeshtasticMessageReceivedAsync;

        public async Task EnsureMqttConnectedAsync(CancellationToken ct = default)
        {
            if (_client?.IsConnected == true) return;
            await _connectLock.WaitAsync(ct);
            try
            {
                if (_client?.IsConnected == true) return;
                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }

                _client = _factory.CreateMqttClient();
                _client.ApplicationMessageReceivedAsync += HandleMqttMessageAsync;
                _client.DisconnectedAsync += async e =>
                {
                    _logger.LogWarning("MQTT disconnected: {Reason}", e.Reason);
                    // attempt reconnect after short delay
                    await Task.Delay(2000);
                    await EnsureMqttConnectedAsync(CancellationToken.None);
                };

                var builder = new MqttClientOptionsBuilder()
                    .WithTcpServer(_options.MqttAddress, _options.MqttPort)
                    .WithCredentials(_options.MqttUser, _options.MqttPassword)
                    .WithClientId("TBot")
                    .WithSessionExpiryInterval(30 * 24 * 3600)
                    .WithCleanSession(false);

                var options = builder.Build();
                var result = await _client.ConnectAsync(options, ct);
                if (result.ResultCode != MqttClientConnectResultCode.Success)
                {
                    _logger.LogError("Failed to connect MQTT: {Code}", result.ResultCode);
                    return;
                }
                _logger.LogInformation("MQTT connected to {Host}:{Port}", _options.MqttAddress, _options.MqttPort);

                await _client.SubscribeAsync(
                    _options.MqttTelegramTopic,
                    MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce,
                    cancellationToken: ct);

                await _client.SubscribeAsync(
                    _options.MqttMeshtasticTopic,
                    MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce,
                    cancellationToken: ct);

                _logger.LogInformation("Subscribed to mqtt topics");
            }
            finally
            {
                _connectLock.Release();
            }
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
                else if (topic == _options.MqttMeshtasticTopic)
                {
                    var payload = arg.ApplicationMessage.Payload;
                    var env = ServiceEnvelope.Parser.ParseFrom(payload);
                    await MeshtasticMessageReceivedAsync?.Invoke(new DataEventArgs<ServiceEnvelope>(env));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling MQTT message");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_client != null)
            {
                await _client.DisconnectAsync(MqttClientDisconnectOptionsReason.AdministrativeAction);
                _client.Dispose();
                _client = null;
            }
            throw new NotImplementedException();
        }
    }
}
