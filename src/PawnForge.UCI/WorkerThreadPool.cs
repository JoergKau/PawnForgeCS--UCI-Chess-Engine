using System;
using System.Threading;
using System.Threading.Tasks;

namespace PawnForge.UCI
{
    /// <summary>
    /// Worker pool for the parallel search: raises the .NET thread-pool
    /// minimum to the requested count and queues search work items.
    /// Each queued item runs on a DEDICATED background thread (not the
    /// shared .NET pool) so a long search never starves other tasks.
    /// Configures .NET ThreadPool (which is already persistent) to match N threads.
    /// Uses Task.Run directly - no custom threads needed.
    /// Update Feb 16: Uses explicit Thread creation via TaskCompletionSource to guarantee
    /// immediate execution and avoid ThreadPool contentions/starvation.
    /// </summary>
    public class WorkerThreadPool : IDisposable
    {
        private readonly int _threadCount;

        public WorkerThreadPool(int threadCount)
        {
            _threadCount = Math.Max(1, Math.Min(threadCount, 256));
            
            // Configure .NET ThreadPool minimum threads to match requested count + reserve
            // This ensures auxiliary tasks (like UCI handlers) don't starve
            ThreadPool.GetMinThreads(out int workerThreads, out int ioThreads);
            if (workerThreads < _threadCount + 2)
            {
                ThreadPool.SetMinThreads(_threadCount + 2, ioThreads);
            }
        }

        /// <summary>
        /// Queue work to execute using a dedicated thread.
        /// Returns Task that completes when work completes.
        /// </summary>
        public Task<T> QueueWork<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            
            var thread = new Thread(() =>
            {
                try
                {
                    var result = work();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            
            thread.IsBackground = true;
            thread.Name = "PawnForge_Worker";
            thread.Start();
            
            return tcs.Task;
        }

        /// <summary>
        /// Shutdown (no-op for global ThreadPool).
        /// </summary>
        public void Shutdown()
        {
            // Nothing to shutdown as threads terminate naturally
        }

        public void Dispose()
        {
            Shutdown();
        }
    }
}
