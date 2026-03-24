using Meshtastic.Protobufs;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TBot.Models;
using TBot.Models.Queue;

namespace TBot
{
    public class LocalMessageQueueService
    {
        public LocalMessageQueueService(IOptions<TBotOptions> options)
        {
            _options = options.Value;
            _delayMs = 60_000 / _options.MeshtasticMaxOutgoingMessagesPerMinute;
            SingleMessageQueueDelay = TimeSpan.FromMilliseconds(_delayMs);
        }

        public TimeSpan SingleMessageQueueDelay { get; private set; }
        private readonly TBotOptions _options;
        private readonly int _delayMs;
        private readonly int _loopDelayMs = 50;
        private Task _processingTask;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        private readonly ConcurrentDictionary<int, PriorityDelayedQueue> _queues = new();
        private readonly SemaphoreSlim _messageSemaphore = new(0);

        public event Func<DataEventArgs<QueuedMessage>, Task> SendMessage;

        public TimeSpan EnqueueMessage(QueuedMessage message, MessagePriority priority)
        {
            _queues.GetOrAdd(message.NetworkId, _ => new PriorityDelayedQueue())
                .Enqueue(message, priority);
            _messageSemaphore.Release();
            var delay = EstimateDelay(message.NetworkId, priority);
            return delay;
        }

        private DequeueResultWithDelay TryDequeueMessage(out QueuedMessage message)
        {
            var now = DateTime.UtcNow;
            TimeSpan minDelay = TimeSpan.MaxValue;
            foreach (var queue in _queues.Values)
            {
                var res = queue.TryDequeue(now, SingleMessageQueueDelay, out message);
                if (res.Result == DequeueResult.Yes)
                {
                    return res;
                }
                else if (res.Result == DequeueResult.Delay && res.Delay < minDelay)
                {
                    minDelay = res.Delay;
                }
            }
            message = null;
            return minDelay != TimeSpan.MaxValue ?
                new DequeueResultWithDelay
                {
                    Result = DequeueResult.Delay,
                    Delay = minDelay
                }
                : new DequeueResultWithDelay
                {
                    Result = DequeueResult.No,
                    Delay = TimeSpan.Zero
                };
        }

        public void Start()
        {
            _processingTask = Task.Run(async () =>
            {
                var token = _cancellationTokenSource.Token;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await _messageSemaphore.WaitAsync(token); // Wait for next message arrival
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            if (!await Loop(token))
                            {
                                break; // No more messages to process
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception)
                        {
                            await Task.Delay(_delayMs);
                        }
                    }
                }
            });
        }

        public Task Stop()
        {
            if (_processingTask == null) return Task.CompletedTask;
            _cancellationTokenSource.Cancel();
            return _processingTask;
        }

        public int GetQueueLength(int networkId) => _queues.TryGetValue(networkId, out var queue) ? queue.Count : 0;

        public TimeSpan EstimateDelay(int networkId, MessagePriority priority)
        {
            var queue = _queues.TryGetValue(networkId, out var q) ? q : null;
            if (queue == null)
            {
                return TimeSpan.Zero;
            }

            int queuedCount = queue.CountPriority(priority);
            var lastDequeueTime = queue.LastDequeueTime;
            var estimatedMs = queuedCount * _delayMs;
            var elapsedMs = (long)(DateTime.UtcNow - lastDequeueTime).TotalMilliseconds;
            if (lastDequeueTime != DateTime.MinValue && elapsedMs < _delayMs)
            {
                estimatedMs += _delayMs - (int)elapsedMs;
            }
            else if (estimatedMs >= _delayMs)
            {
                estimatedMs -= _delayMs;
            }
            return TimeSpan.FromMilliseconds(estimatedMs);
        }

        private async ValueTask<bool> Loop(CancellationToken token)
        {
            var result = TryDequeueMessage(out var message);

            switch (result.Result)
            {
                case DequeueResult.No:
                    return false; // No messages to process
                case DequeueResult.Delay:
                    {
                        var delayMs = 
                            Math.Min((int)result.Delay.TotalMilliseconds, _loopDelayMs)
                            + 1/*To prevent 0 delay*/;
                        try
                        {
                            await Task.Delay(delayMs, token); // Wait before retrying
                        }
                        catch (OperationCanceledException)
                        {
                            return false;
                        }
                        return true;
                    }
                case DequeueResult.Yes:
                    {
                        if (SendMessage != null)
                        {
                            await SendMessage(new DataEventArgs<QueuedMessage>(message));
                        }
                        return true;
                    }
                default:
                    throw new NotImplementedException($"Unexpected DequeueResult - {result.Result}");
            }
        }
    }
}
