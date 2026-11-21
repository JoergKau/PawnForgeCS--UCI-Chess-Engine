using System.Runtime.InteropServices;

namespace PawnForge.Core.Search
{
    /// <summary>
    /// Thread-local node counter with explicit cache-line padding to prevent
    /// FALSE SHARING: without the padding, two threads' counters could land in
    /// the same 64-byte cache line and every node increment would invalidate
    /// the other thread's line - measurable slowdown at 4+ threads.
    ///
    /// LAYOUT ([StructLayout(Explicit)]): 64 bytes of padding BEFORE the
    /// counter (offset 64) and 64 bytes AFTER (through offset 135, size 136)
    /// guarantee the counter owns its cache line regardless of the CLR's
    /// object-header placement.
    ///
    /// The counter is a PLAIN long: no atomics, no interlocked operations.
    /// Each thread owns its instance exclusively; aggregation happens only
    /// when the main thread reports an info line (see EngineOrchestrator).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 136)] // 64 (pre) + 8 (Nodes) + 64 (post)
    public sealed class ThreadMetrics
    {
#pragma warning disable CS0169, CS0649 // Field is never used
        [FieldOffset(0)]  private long _p0;
        [FieldOffset(8)]  private long _p1;
        [FieldOffset(16)] private long _p2;
        [FieldOffset(24)] private long _p3;
        [FieldOffset(32)] private long _p4;
        [FieldOffset(40)] private long _p5;
        [FieldOffset(48)] private long _p6;
        [FieldOffset(56)] private long _p7;

        /// <summary>The node counter (offset 64 - its own cache line).</summary>
        [FieldOffset(64)] public long Nodes;

        [FieldOffset(72)] private long _p8;
        [FieldOffset(80)] private long _p9;
        [FieldOffset(88)] private long _p10;
        [FieldOffset(96)] private long _p11;
        [FieldOffset(104)] private long _p12;
        [FieldOffset(112)] private long _p13;
        [FieldOffset(120)] private long _p14;
        [FieldOffset(128)] private long _p15;
#pragma warning restore CS0169, CS0649

        /// <summary>Non-atomic increment - safe because only the owning thread writes.</summary>
        public void IncrementNodes() => Nodes++;

        /// <summary>Resets the counter (between searches).</summary>
        public void Reset() => Nodes = 0;

        /// <summary>Debug helper: reads the padding so the JIT cannot optimize it away.</summary>
        public long PreventOptimization()
        {
            return _p0 + _p1 + _p2 + _p3 + _p4 + _p5 + _p6 + _p7 +
                   _p8 + _p9 + _p10 + _p11 + _p12 + _p13 + _p14 + _p15;
        }
    }
}
