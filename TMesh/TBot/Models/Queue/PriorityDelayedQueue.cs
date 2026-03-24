using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models.Queue
{
    public class PriorityDelayedQueue
    {
        private readonly ConcurrentQueue<QueuedMessage> _highPriorityQueue = new();
        private readonly ConcurrentQueue<QueuedMessage> _normalPriorityQueue = new();
        private readonly ConcurrentQueue<QueuedMessage> _lowPriorityQueue = new();
        private DateTime _lastDequeueTime = DateTime.MinValue;
        private readonly ConcurrentQueue<QueuedMessage>[] _queues;

        public DateTime LastDequeueTime => _lastDequeueTime;

        public PriorityDelayedQueue()
        {
            _queues = [_highPriorityQueue, _normalPriorityQueue, _lowPriorityQueue];
        }

        public void Enqueue(QueuedMessage message, MessagePriority priority)
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

        public DequeueResultWithDelay TryDequeue(DateTime now, TimeSpan minElapsed, out QueuedMessage message)
        {
            var elapsed = now - _lastDequeueTime;
            if (elapsed < minElapsed)
            {
                message = null;
                return new DequeueResultWithDelay
                {
                    Result = DequeueResult.Delay,
                    Delay = minElapsed - elapsed
                };
            }
            for (int i = 0; i < _queues.Length; i++)
            {
                if (_queues[i].TryDequeue(out message))
                {
                    _lastDequeueTime = now;
                    return new DequeueResultWithDelay
                    {
                        Result = DequeueResult.Yes,
                        Delay = TimeSpan.Zero
                    };
                }
            }
            message = null;
            return new DequeueResultWithDelay
            {
                Result = DequeueResult.No,
                Delay = TimeSpan.Zero
            };
        }

        public int Count => _highPriorityQueue.Count + _normalPriorityQueue.Count + _lowPriorityQueue.Count;

        public int CountPriority(MessagePriority priority)
        {
            return priority switch
            {
                MessagePriority.High => _highPriorityQueue.Count,
                MessagePriority.Normal => _highPriorityQueue.Count + _normalPriorityQueue.Count,
                MessagePriority.Low => _highPriorityQueue.Count + _normalPriorityQueue.Count + _lowPriorityQueue.Count,
                _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null)
            };
        }
    }
}
