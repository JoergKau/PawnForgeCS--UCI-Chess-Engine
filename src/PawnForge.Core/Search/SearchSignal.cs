
using System.Threading;

namespace PawnForge.Core.Search
{
    /// <summary>
    /// Shared stop signal coordinating ALL search threads (Lazy SMP): one
    /// int-backed flag with volatile semantics (via Atomics.ReadBool/
    /// WriteBool). Polled by every thread every 2048 nodes; a hard-timeout
    /// on the main thread or a UCI `stop` sets it for everyone at once.
    /// Reset before each search (a fresh instance per search is created by
    /// the orchestrator to avoid stale flags racing the start).
    /// </summary>
    public class SearchSignal
    {
        private int _stop = 0; // 0 = false, 1 = true

        /// <summary>True when all search threads should stop at the next poll.</summary>
        public bool ShouldStop => Atomics.ReadBool(ref _stop);

        /// <summary>Sets the stop flag (UCI `stop`, time expiry, node limit, mate target).</summary>
        public void Stop()
        {
            Atomics.WriteBool(ref _stop, true);
        }

        /// <summary>Clears the stop flag (not used by the orchestrator - it creates a fresh signal per search).</summary>
        public void Reset()
        {
            Atomics.WriteBool(ref _stop, false);
        }
    }
}
