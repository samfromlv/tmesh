using Meshtastic.Protobufs;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private readonly ConcurrentQueue<QueuedMessage> _highPriorityQueue = new ConcurrentQueue<QueuedMessage>();
        private readonly ConcurrentQueue<QueuedMessage> _normalPriorityQueue = new ConcurrentQueue<QueuedMessage>();
        private readonly ConcurrentQueue<QueuedMessage> _lowPriorityQueue = new ConcurrentQueue<QueuedMessage>();

        public event Func<DataEventArgs<QueuedMessage>, Task> SendMessage;
        public void EnqueueMessage(QueuedMessage message, MessagePriority priority )
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
        }

        private bool TryDequeueMessage(out QueuedMessage message)
        {
            if (_highPriorityQueue.TryDequeue(out message))
            {
                return true;
            }
            if (_normalPriorityQueue.TryDequeue(out message))
            {
                return true;
            }
            if (_lowPriorityQueue.TryDequeue(out message))
            {
                return true;
            }
            message = null;
            return false;
        }

        public void Start()
        {
            _processingTask = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    var timeSinceLastSent = DateTime.UtcNow - _lastSentTime;
                    if (timeSinceLastSent.TotalMilliseconds < _delayMs)
                    {
                        var delay = _delayMs - (int)timeSinceLastSent.TotalMilliseconds;
                        await Task.Delay(delay);
                    }
                    else
                    {
                        await Task.Delay(100);
                    }
                    await Loop();
                }
            });
        }

        public Task Stop()
        {
            if (_processingTask == null)
                return Task.CompletedTask;

            _cancellationTokenSource.Cancel();
            return _processingTask;
        }


        public async Task Loop()
        {
            if (!TryDequeueMessage(out var message))
            {
                return;
            }

            await SendMessage?.Invoke(new DataEventArgs<QueuedMessage>(message));
            _lastSentTime = DateTime.UtcNow;
        }
    }
}
