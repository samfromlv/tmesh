using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MQTTnet;
using Shared.Models;
using System.Text.Json;
using MQTTnet.Protocol;

namespace TProxy;

public class MqttService : IAsyncDisposable
{
    private readonly TProxyOptions _options;
    private readonly IMqttClient _client;
    private readonly ILogger<MqttService> _logger;
    private readonly ConcurrentQueue<(string Topic, string Payload)> _pending = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private bool _connected;
    private Task _reconnectLoopTask;
    private DateTime _started;

    public MqttService(IOptions<TProxyOptions> options, ILogger<MqttService> logger)
    {
        _options = options.Value;
        _logger = logger;
        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
        _reconnectLoopTask = Task.Run(ReconnectLoopAsync);
        _started = DateTime.UtcNow;
    }

    public BotStats LastStatusPayload { get; private set; }

    public DateTime Started => _started;

    private Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
       if (!args.ApplicationMessage.Topic.Equals(_options.MqttStatusTopic, StringComparison.OrdinalIgnoreCase))
           return Task.CompletedTask;

       LastStatusPayload = JsonSerializer.Deserialize<BotStats>(args.ApplicationMessage.ConvertPayloadToString());
       return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
    {
        _connected = false;
        _logger.LogWarning("MQTT disconnected. Reason: {Reason}", arg.Reason);
        return Task.CompletedTask;
    }

    private async Task EnsureConnectedAsync()
    {
        if (_connected) return;
        if (!await _connectLock.WaitAsync(0))
        {
            // Another thread is handling connect, wait briefly
            await _connectLock.WaitAsync();
            _connectLock.Release();
            return; // connection state updated by other thread
        }
        try
        {
            if (_connected) return;

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
            var mqttOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(_options.MqttAddress, _options.MqttPort)
                .WithTlsOptions(sslOptions)
                .WithCredentials(_options.MqttUser, _options.MqttPassword)
                .WithCleanSession()
                .Build();

            try
            {
                var result = await _client.ConnectAsync(mqttOptions, CancellationToken.None);
                _connected = result.ResultCode == MqttClientConnectResultCode.Success;
                if (_connected)
                {
                    var topicFilter = new MqttTopicFilterBuilder()
                        .WithTopic(_options.MqttStatusTopic)
                        .WithAtLeastOnceQoS()
                        .WithRetainHandling(MqttRetainHandling.SendAtSubscribe)
                        .Build();

                    await _client.SubscribeAsync(topicFilter);

                    _logger.LogInformation("Connected to MQTT broker {Host}:{Port}", _options.MqttAddress, _options.MqttPort);
                    await FlushPendingAsync();
                }
                else
                {
                    _logger.LogError("Failed to connect to MQTT broker: {ResultCode}", result.ResultCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MQTT connect attempt failed");
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default)
    {
        // Try connect
        await EnsureConnectedAsync();
        if (_connected)
        {
            try
            {
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(Encoding.UTF8.GetBytes(payload))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                var result = await _client.PublishAsync(message, cancellationToken);
                if (result.ReasonCode != MqttClientPublishReasonCode.Success)
                {
                    _logger.LogWarning("Publish finished with reason: {Reason}, queuing message", result.ReasonCode);
                    QueueMessage(topic, payload);
                }
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Publish failed, queuing message for retry");
                _connected = false; // force reconnect
                QueueMessage(topic, payload);
                return;
            }
        }
        // Not connected, queue
        QueueMessage(topic, payload);
    }

    private void QueueMessage(string topic, string payload)
    {
        _pending.Enqueue((topic, payload));
        _logger.LogDebug("Queued message for topic {Topic}. Pending count: {Count}", topic, _pending.Count);
    }

    private async Task FlushPendingAsync()
    {
        if (!_connected) return;
        var flushed = 0;
        while (_pending.TryDequeue(out var item))
        {
            try
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(item.Topic)
                    .WithPayload(Encoding.UTF8.GetBytes(item.Payload))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();
                var publishResult = await _client.PublishAsync(msg, CancellationToken.None);
                if (publishResult.ReasonCode != MqttClientPublishReasonCode.Success)
                {
                    _logger.LogWarning("Failed to flush queued message. Reason: {Reason}. Re-queueing.", publishResult.ReasonCode);
                    _pending.Enqueue(item); // put back
                    break; // stop flush, maybe connection degraded
                }
                flushed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception flushing queued message. Re-queueing and stopping flush.");
                _pending.Enqueue(item);
                _connected = false; // trigger reconnect
                break;
            }
        }
        if (flushed > 0)
        {
            _logger.LogInformation("Flushed {Count} queued MQTT messages", flushed);
        }
    }

    private async Task ReconnectLoopAsync()
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!_connected)
                {
                    await EnsureConnectedAsync();
                }
                else if (!_pending.IsEmpty)
                {
                    await FlushPendingAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reconnect loop iteration error");
            }
            await Task.Delay(delay, _cts.Token).ContinueWith(_ => { });
            // simple backoff if not connected
            if (!_connected && delay < TimeSpan.FromSeconds(30))
            {
                delay += TimeSpan.FromSeconds(2);
            }
            else if (_connected)
            {
                delay = TimeSpan.FromSeconds(2);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _reconnectLoopTask;
        }
        catch { }
        if (_client.IsConnected)
        {
            try
            {
                await _client.DisconnectAsync();
            }
            catch { }
        }
        _cts.Dispose();
        _connectLock.Dispose();
    }
}
