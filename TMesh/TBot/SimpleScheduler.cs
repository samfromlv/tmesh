using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot
{
    public class SimpleScheduler(ILogger<SimpleScheduler> logger) : IDisposable
    {
        private readonly LinkedList<CancellationTokenSource> _items = new();
        private readonly object _lock = new();

        /// <summary>
        /// Schedule an Action to run once after the given delay.
        /// </summary>
        public void Schedule(TimeSpan delay, Func<Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            var cts = new CancellationTokenSource();
            LinkedListNode<CancellationTokenSource> node;

            lock (_lock)
            {
                node = _items.AddLast(cts);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, cts.Token);
                    if (!cts.IsCancellationRequested)
                    {
                        await action();
                    }
                }
                catch (TaskCanceledException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in scheduled action");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (node!.List != null) // still in list
                            _items.Remove(node);
                    }
                    cts.Dispose();
                }
            });
        }

        /// <summary>
        /// Cancel all scheduled work (for app shutdown).
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var cts in _items)
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                _items.Clear();
            }
            GC.SuppressFinalize(this);
        }
    }

}
