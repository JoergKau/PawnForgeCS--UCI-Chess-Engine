namespace PawnForge.Tests;

using PawnForge.Core.Search;
using Xunit;

/// <summary>
/// Locks for / G1:
/// the **clock-imbalance time-management scaler** — the the design reference pattern of spending MORE time
/// when ahead on the clock and LESS when behind, clipped by the existing hard limit.
///
/// It SUPERSEDES the older one-sided TM3 (<see cref="TimeManager.UseTimeAdvantage"/>, default
/// ON), which only reinvests a surplus and does nothing when behind; running both would
/// double-count the ahead side, so the G1 path takes precedence when its toggle is on.
///
/// Invariants under test:
/// * default OFF ⇒ the shipped TM behaviour (bit-identical; no clock-imbalance math runs);
/// * ON ⇒ monotone in the clock ratio: ahead scales up, behind scales down, level is inert;
/// * ON ⇒ the ZERO-FLAG guarantee survives: soft ≤ hard and hard ≤ myTime/4 always, and the
/// deficit side never starves the move below the allocation's own safety floor;
/// * the scales are respected as caps (a 10:1 split cannot exceed AheadScale);
/// * the toggle must be an ADVERTISED uci option (an unadvertised lever is unreachable —
/// the SPSA v1 void), and — unlike every other item — its verdict gate is a
/// TIMED match, not a fixed-depth bench (it never runs under `go bench`).
/// </summary>
public class ClockImbalanceTests
{
    private static long AllocOf(TimeManager tm) => tm.SoftLimit - tm.StartTime;

    private static TimeManager New(bool clockImbalance, long wtime, long btime, long winc = 0, long binc = 0,
                                   int movesToGo = 0, int side = 0, int overhead = 0)
    {
        var tm = new TimeManager { UseClockImbalance = clockImbalance };
        tm.Init(wtime, btime, winc, binc, movesToGo, side, TimeManager.NowMs(), overhead);
        return tm;
    }

    // ── defaults + delivery ───────────────────────────────────────────────────────────

    [Fact]
    public void G1_Defaults_Off_WithReferenceLikeScales()
    {
        var tm = new TimeManager();
        Assert.False(tm.UseClockImbalance);
        Assert.Equal(50, tm.ClockImbalanceAheadScale);
        Assert.Equal(50, tm.ClockImbalanceBehindScale);
    }

    [Fact]
    public void G1_AdvertisedInHandshakeWithMatchingDefaults()
    {
        var orchestrator = new PawnForge.UCI.EngineOrchestrator();
        Assert.True(orchestrator.Options.TryGetOption(nameof(Search.UseClockImbalance), out var on));
        Assert.Equal("false", on!.DefaultValue);
        Assert.True(orchestrator.Options.TryGetOption(nameof(Search.ClockImbalanceAheadScale), out var ahead));
        Assert.Equal("50", ahead!.DefaultValue);
        Assert.True(orchestrator.Options.TryGetOption(nameof(Search.ClockImbalanceBehindScale), out var behind));
        Assert.Equal("50", behind!.DefaultValue);
    }

    // ── behaviour-neutral at the default ──────────────────────────────────────────────

    [Fact]
    public void G1_Off_IsBitIdenticalToTheShippedTm()
    {
        // Same clock inputs, toggle left at its default vs set explicitly false:
        // both must reproduce the shipped soft/hard limits exactly.
        var implicitOff = New(clockImbalance: false, wtime: 180_000, btime: 20_000);
        var shipped = new TimeManager();
        shipped.Init(180_000, 20_000, 0, 0, 0, 0, TimeManager.NowMs(), 0);

        Assert.Equal(AllocOf(shipped), AllocOf(implicitOff));
        Assert.Equal(shipped.HardLimit - shipped.StartTime, implicitOff.HardLimit - implicitOff.StartTime);
    }

    [Fact]
    public void G1_LevelClock_IsInert()
    {
        // ratio == 0.5 ⇒ scale == 1.0 ⇒ G1 must not change a dead-level allocation.
        var off = New(clockImbalance: false, wtime: 60_000, btime: 60_000);
        var on = New(clockImbalance: true, wtime: 60_000, btime: 60_000);
        Assert.Equal(AllocOf(off), AllocOf(on));
    }

    // ── the two-sided scaler ──────────────────────────────────────────────────────────

    [Fact]
    public void G1_Ahead_SpendsMore_Behind_SpendsLess()
    {
        long level = AllocOf(New(clockImbalance: true, wtime: 60_000, btime: 60_000));
        long ahead = AllocOf(New(clockImbalance: true, wtime: 100_000, btime: 20_000));
        long behind = AllocOf(New(clockImbalance: true, wtime: 20_000, btime: 100_000));

        Assert.True(ahead > level, $"ahead ({ahead}) must exceed level ({level})");
        Assert.True(behind < level, $"behind ({behind}) must be below level ({level})");
    }

    [Fact]
    public void G1_ScalesAreRespectedAsCaps()
    {
        // 10 s vs 990 s is a ~1:99 ratio: the ahead scale must cap at 1 + AheadScale/100 = 1.5×
        // (the TM3-era ceiling), and the behind side at 1 − BehindScale/100.
        var cappedAhead = new TimeManager { UseClockImbalance = true, ClockImbalanceAheadScale = 50, ClockImbalanceBehindScale = 50 };
        cappedAhead.Init(wtime: 990_000, btime: 10_000, 0, 0, 0, 0, TimeManager.NowMs(), 0);

        // The allocation is capped by the safety buffer / hard limit anyway, but the SCALE
        // bound is what matters: it must equal the level allocation × <= 1.5.
        long levelAlloc = AllocOf(New(clockImbalance: true, wtime: 990_000, btime: 990_000));
        long aheadAlloc = AllocOf(cappedAhead);
        Assert.True(aheadAlloc <= (long)(levelAlloc * 1.5) + 2,
            $"ahead allocation {aheadAlloc} exceeded 1.5× the level allocation {levelAlloc}");
    }

    // ── the zero-flag guarantee survives ──────────────────────────────────────────────

    [Fact]
    public void G1_Behind_NeverBreaksHardLimitOrStarves()
    {
        // Deeply behind on the clock: soft ≤ hard ≤ myTime/4 must still hold, and the
        // allocation must stay positive.
        var tm = New(clockImbalance: true, wtime: 10_000, btime: 600_000);
        long alloc = AllocOf(tm);
        Assert.True(alloc > 0, "the deficit side must never produce a non-positive allocation");
        Assert.True(tm.SoftLimit <= tm.HardLimit, "soft must never exceed hard");
        Assert.True(tm.HardLimit - tm.StartTime <= 10_000 / 4 + 1, "hard must stay within myTime/4");
    }

    [Fact]
    public void G1_UnknownOrTinyOpponentClock_IsIgnored()
    {
        // theirTime == 0 (no clock info) ⇒ no scaling; tiny clocks (< 5 s floor) ⇒ no scaling.
        var off = New(clockImbalance: false, wtime: 60_000, btime: 0);
        var on = New(clockImbalance: true, wtime: 60_000, btime: 0);
        Assert.Equal(AllocOf(off), AllocOf(on));

        var offTiny = New(clockImbalance: false, wtime: 4_000, btime: 2_000);
        var onTiny = New(clockImbalance: true, wtime: 4_000, btime: 2_000);
        Assert.Equal(AllocOf(offTiny), AllocOf(onTiny));
    }

    [Fact]
    public void G1_SupersedesTm3_NoDoubleCounting()
    {
        // With both toggles ON the ahead side must use ONLY the G1 scale: the allocation must
        // equal the one produced with TM3 explicitly disabled.
        var both = new TimeManager { UseClockImbalance = true, UseTimeAdvantage = true };
        both.Init(180_000, 60_000, 0, 0, 0, 0, TimeManager.NowMs(), 0);

        var g1Only = new TimeManager { UseClockImbalance = true, UseTimeAdvantage = false };
        g1Only.Init(180_000, 60_000, 0, 0, 0, 0, TimeManager.NowMs(), 0);

        Assert.Equal(AllocOf(g1Only), AllocOf(both));
    }

    [Fact]
    public void G1_TournamentMode_AlsoScales()
    {
        // movesToGo > 0 (a real time control with a move quota) must scale too — the review's
        // item is not sudden-death-only, unlike TM3.
        long level = AllocOf(New(clockImbalance: true, wtime: 120_000, btime: 120_000, movesToGo: 40));
        long behind = AllocOf(New(clockImbalance: true, wtime: 30_000, btime: 120_000, movesToGo: 40));
        Assert.True(behind < level, $"behind ({behind}) must be below level ({level}) in tournament mode");
    }
}
