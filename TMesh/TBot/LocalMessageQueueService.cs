using Meshtastic.Protobufs;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TBot.Models;

namespace TBot
{
    public class LocalMessageQueueService
    {
        public LocalMessageQueueService(IOptions<TBotOptions> options)
        {
            _options = options.Value;
            _delayMs = 60_000 / _options.MeshtasticMaxOutgoingMessagesPerMinute;
        }

        private readonly TBotOptions _options;
        private readonly int _delayMs;
        private DateTime _lastSentTime = DateTime.MinValue;
        private Task _processingTask;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        private readonly ConcurrentQueue<QueuedMessage> _highPriorityQueue = new();
        private readonly ConcurrentQueue<QueuedMessage> _normalPriorityQueue = new();
        private readonly ConcurrentQueue<QueuedMessage> _lowPriorityQueue = new();
        private readonly SemaphoreSlim _messageSemaphore = new(0);

        public event Func<DataEventArgs<QueuedMessage>, Task> SendMessage;

        public TimeSpan EnqueueMessage(QueuedMessage message, MessagePriority priority)
        {
            switch (priority)
            {
                case MessagePriority.High:
                    _highPriorityQueue.Enqueue(message);
                    break;
                case MessagePriority.Normal:
                    _normalPriorityQueue.Enqueue(message);
                    break;
                default:
                    _lowPriorityQueue.Enqueue(message);
                    break;
            }
            _messageSemaphore.Release();
            var delay = EstimateDelay(priority);
            return delay;
        }

        private bool TryDequeueMessage(out QueuedMessage message)
        {
            if (_highPriorityQueue.TryDequeue(out message)) return true;
            if (_normalPriorityQueue.TryDequeue(out message)) return true;
            if (_lowPriorityQueue.TryDequeue(out message)) return true;
            message = null;
            return false;
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
                        if (!await Loop(token))
                        {
                            break; // No more messages to process
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

        public TimeSpan EstimateDelay(MessagePriority priority)
        {
            int queuedCount = 0;
            switch (priority)
            {
                case MessagePriority.High:
                    queuedCount += _highPriorityQueue.Count;
                    break;
                case MessagePriority.Normal:
                    queuedCount += _highPriorityQueue.Count + _normalPriorityQueue.Count;
                    break;
                case MessagePriority.Low:
                    queuedCount += _highPriorityQueue.Count + _normalPriorityQueue.Count + _lowPriorityQueue.Count;
                    break;
            }
            var estimatedMs = queuedCount * _delayMs;
            var elapsedMs = (int)(DateTime.UtcNow - _lastSentTime).TotalMilliseconds;
            if (_lastSentTime != DateTime.MinValue && elapsedMs < _delayMs)
            {
                estimatedMs += (_delayMs - elapsedMs);
            }
            return TimeSpan.FromMilliseconds(estimatedMs);
        }

        private async ValueTask<bool> Loop(CancellationToken token)
        {
            if (!TryDequeueMessage(out var message))
            {
                return false; // Semaphore count may exceed actual queued messages in rare races
            }

            // Rate limit: only delay if last send was within the window
            var elapsedMs = (int)(DateTime.UtcNow - _lastSentTime).TotalMilliseconds;
            if (_lastSentTime != DateTime.MinValue && elapsedMs < _delayMs)
            {
                var wait = _delayMs - elapsedMs;
                try { await Task.Delay(wait, token); } catch (OperationCanceledException) { return false; }
            }

            if (SendMessage != null)
            {
                await SendMessage(new DataEventArgs<QueuedMessage>(message));
                _lastSentTime = DateTime.UtcNow;
            }
            return true;
        }
    }
}
