namespace PawnForge.Core.Evaluation;

using System.Threading;

// ============================================================================
// NnueRegionCounters.cs — the real-game king-region distribution counter
// ----------------------------------------------------------------------------
// Audit request : the
// pool shares for the rank-5 regions r4/r5 (0.79 % / 1.30 % of POV events) are
// a PROXY from training data; this counter measures the REAL-GAME distribution
// — one u64 per region, incremented on every accumulator refresh — so a bench
// run settles whether the r4/r5 merge is justified on play, not on pool
// data.
//
// WHAT COUNTS AS A REFRESH: `NnueAccumulatorV5.RefreshPov` — the full rebuild
// of ONE POV's accumulator, which happens exactly when that POV's own king
// state changes the region/mirror frame (lazy own-king refresh) or on a cold
// start. Each refresh selects exactly one region (the POV king's header-table
// entry), so the counter is a per-POV-event region histogram — the same unit
// the pool's shares are expressed in (counts POV
// events too).
//
// COST DISCIPLINE: this is a DIAGNOSTIC, and it sits on the refresh path.
// Counting is therefore OPT-IN (`Enable`/`Disable`): when disabled the hook is
// a single predictable branch (measured cost ~0, the perf baseline in
// must stay valid with counters off). The intended
// measurement flow is the UCI commands:
//
// nnueregions reset — clear all counters AND start counting
// bench [depth] [threads] — play through the bench positions
// nnueregions — print the per-region histogram + total
// nnueregions off — stop counting
//
// THREAD SAFETY: Lazy SMP runs one evaluator (hence one accumulator) per
// helper thread, so refreshes happen concurrently. Every increment is an
// Interlocked op on the region's u64 — the histogram is exact, not sampled.
//
// WHY STATIC: the measurement is a property of the RUN, not of one evaluator;
// a shared counter is what a bench (or a match, via fastchess's per-game
// engines) aggregates over. Instances of the evaluator come and go per thread
// and per position; the counters survive them.
// ============================================================================

/// <summary>Opt-in per-region u64 counters over the king-region table — the
/// real-game distribution the pool shares can only estimate.</summary>
public static class NnueRegionCounters
{
    /// <summary>Counter slots. Regions are always &lt; 32 (the header's region
    /// bound mirrors <see cref="NnueFeatureSet2.MaxRegions"/>), so a fixed array
    /// indexed by region id needs no bounds logic on the hot path.</summary>
    private static readonly ulong[] Counts = new ulong[NnueFeatureSet2.MaxRegions];

    /// <summary>Volatile so the disable path is visible across threads without
    /// a lock; counting is opt-in and must never linger after the operator
    /// turns it off.</summary>
    private static volatile bool _enabled;

    /// <summary>Whether refreshes are currently being counted. Diagnostic
    /// sessions only; the compiled defaults (and every perf measurement) run
    /// with counting OFF.</summary>
    public static bool Enabled => _enabled;

    /// <summary>Start counting (idempotent). Does NOT clear the counters —
    /// pair with <see cref="Reset"/> for a clean measurement.</summary>
    public static void Enable() => _enabled = true;

    /// <summary>Stop counting. In-flight increments on other threads may still
    /// land (a benign race on a diagnostic histogram).</summary>
    public static void Disable() => _enabled = false;

    /// <summary>One accumulator refresh of one POV: add 1 to that POV's region.
    /// Called from <see cref="NnueAccumulatorV5.RefreshPov"/> only.</summary>
    public static void Record(int region)
    {
        if (!_enabled)
            return;
        Interlocked.Increment(ref Counts[region]);
    }

    /// <summary>Clear every counter (measurement start). Safe to call while
    /// threads are counting; a concurrent refresh may land either side of the
    /// clear, which only blurs the first event of the run.</summary>
    public static void Reset() => Array.Clear(Counts);

    /// <summary>Read the histogram: the first <paramref name="regions"/> slots
    /// (one per net region) and their total. Read outside the counted run —
    /// the snapshot is not atomic against concurrent increments.</summary>
    public static (long[] Counts, long Total) Snapshot(int regions)
    {
        long[] counts = new long[regions];
        long total = 0;
        for (int r = 0; r < regions; r++)
        {
            counts[r] = (long)Counts[r];
            total += counts[r];
        }
        return (counts, total);
    }
}
