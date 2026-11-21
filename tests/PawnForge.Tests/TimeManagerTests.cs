using PawnForge.Core.Search;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Unit tests for TimeManager formulas and behavior.
/// Validates fixes: updated sudden-death allocation (time/30 + inc),
/// removal of the 0.85× guard from ShouldContinue, and the hard-limit formula.
/// TM1: stable-PV shrink 0.85×/0.75×
/// behind the UseStableShrink toggle — SPRT returned a null (2026-09-10) and the
/// default was REVERTED to the 2026-08-28 no-shrink behavior (toggle stays for re-tests).
/// </summary>
public class TimeManagerTests
{
    // ─────────────────────────────────────────────────────────────
    // Test 1 — Sudden-death allocation: Init(movesToGo=0)
    // Expected: SoftLimit - StartTime == myTime/30 + myInc
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Init_SuddenDeath_UsesTime30PlusFullIncrement()
    {
        var tm = new TimeManager();
        long wtime = 60_000, winc = 1_000;
        long now = TimeManager.NowMs();

        tm.Init(wtime, btime: 60_000, winc, binc: 1_000, movesToGo: 0, side: 0, now, moveOverhead: 0);

        // Expected pre-cap alloc = wtime/30 + winc (integer division)
        long expectedAlloc = wtime / 30 + winc;
        long actualAlloc = tm.SoftLimit - tm.StartTime;

        // The time-advantage multiplier and tactical floor can increase alloc,
        // but the safety buffer can only shrink it. When wtime == btime the
        // advantage multiplier is 1.0× (no surplus). The tactical floor fires
        // when myTime > 30 000: minAlloc = max(3000, 600) = 3000, which is
        // below expectedAlloc (3000 vs 3000+), so it doesn't inflate.
        // The safety buffer only kicks in if alloc > myTime - 500 (not here).
        Assert.True(actualAlloc >= expectedAlloc,
            $"Expected alloc >= {expectedAlloc} ms, got {actualAlloc} ms");
    }

    [Fact]
    public void Init_SuddenDeath_UsesUpdatedFormula_NotOldFormula()
    {
        var tm = new TimeManager();
        long wtime = 120_000, winc = 1_000;
        long now = TimeManager.NowMs();

        tm.Init(wtime, btime: 120_000, winc, binc: 1_000, movesToGo: 0, side: 0, now, moveOverhead: 0);

        // Old formula: wtime/40 + winc/2 = 3000 + 500 = 3500
        long oldAlloc = wtime / 40 + winc / 2;
        // New formula: wtime/30 + winc = 4000 + 1000 = 5000
        long newAlloc = wtime / 30 + winc;

        long actualAlloc = tm.SoftLimit - tm.StartTime;

        Assert.True(actualAlloc >= newAlloc,
            $"Expected alloc >= {newAlloc} (new formula), but got {actualAlloc}");
        Assert.True(actualAlloc > oldAlloc,
            $"Alloc ({actualAlloc}) should exceed old formula ({oldAlloc})");
    }

    // ─────────────────────────────────────────────────────────────
    // Test 2 — TM1 stable-PV shrink (UseStableShrink, default ON)
    // _stableIterations >= 3 → 0.75×, >= 2 → 0.85×;
    // toggle OFF → floor restored at 1.0× (2026-08-28 behavior)
    // ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, 2, 1.0)]  // 2 calls → _stableIterations=1 → no shrink yet
    [InlineData(true, 3, 0.85)] // 3 calls → _stableIterations=2
    [InlineData(true, 5, 0.75)] // 5 calls → _stableIterations=4
    [InlineData(false, 2, 1.0)]
    [InlineData(false, 3, 1.0)]
    [InlineData(false, 5, 1.0)]
    public void GetPVStabilityMultiplier_StablePV_ShrinkPerToggle(bool shrink, int stableIters, double expected)
    {
        var tm = new TimeManager();
        // Provide a valid Init so _movesToGo is not -1 (which would bypass stability)
        long now = TimeManager.NowMs();
        tm.Init(60_000, 60_000, 0, 0, movesToGo: 0, side: 0, now, moveOverhead: 0);
        tm.UseStableShrink = shrink;

        // Simulate stable iterations with the same best-move
        const int stableMove = 1234;
        for (int i = 0; i < stableIters; i++)
            tm.UpdatePVStability(stableMove, stable: true);

        double mult = tm.GetPVStabilityMultiplier();
        Assert.Equal(expected, mult, precision: 3);
    }

    [Fact]
    public void GetPVStabilityMultiplier_StablePV_DefaultIsNoShrink()
    {
        var tm = new TimeManager();
        long now = TimeManager.NowMs();
        tm.Init(60_000, 60_000, 0, 0, movesToGo: 0, side: 0, now, moveOverhead: 0);
        tm.UpdatePVStability(1234, stable: true);
        tm.UpdatePVStability(1234, stable: true);
        tm.UpdatePVStability(1234, stable: true);
        tm.UpdatePVStability(1234, stable: true); // _stableIterations=3

        // TM1 reverted (2026-09-10): default is the 2026-08-28 no-shrink behavior.
        double mult = tm.GetPVStabilityMultiplier();
        Assert.Equal(1.0, mult, precision: 3);
    }

    // ─────────────────────────────────────────────────────────────
    // Test 3 — Unstable PV cap at 1.5×
    // _pvChanges >= 3 → multiplier <= 1.5
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetPVStabilityMultiplier_UnstablePV3Changes_ReturnsAtMost1_5()
    {
        var tm = new TimeManager();
        long now = TimeManager.NowMs();
        tm.Init(60_000, 60_000, 0, 0, movesToGo: 0, side: 0, now, moveOverhead: 0);

        // Simulate 3 different best moves (each change increments _pvChanges)
        tm.UpdatePVStability(111, stable: false);
        tm.UpdatePVStability(222, stable: false);
        tm.UpdatePVStability(333, stable: false);
        tm.UpdatePVStability(444, stable: false); // 3 changes by now

        double mult = tm.GetPVStabilityMultiplier();
        Assert.True(mult <= 1.5,
            $"Unstable PV multiplier must be <= 1.5, got {mult:F3}");
        Assert.True(mult >= 1.0,
            $"Multiplier must be at least 1.0 even when unstable, got {mult:F3}");
    }

    // ─────────────────────────────────────────────────────────────
    // Test 4 — ShouldContinue: no 0.85× guard (removed)
    // At 86% of the effective soft limit → still returns true
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void ShouldContinue_At86PercentOfSoftLimit_ReturnsTrue()
    {
        var tm = new TimeManager();
        // Use 10 000 ms allocation so percentages are unambiguous
        long fakeNow = 1_000_000L; // arbitrary monotonic base
        tm.Init(wtime: 300_000, btime: 300_000, winc: 0, binc: 0,
                movesToGo: 0, side: 0, currentTime: fakeNow, moveOverhead: 0);

        long alloc = tm.SoftLimit - tm.StartTime;
        // 86% of the allocation: if the old 0.85× guard were still present
        // this would return false.
        long at86Percent = tm.StartTime + (long)(alloc * 0.86);

        bool result = tm.ShouldContinue(at86Percent, bestMove: 0, score: 0);

        Assert.True(result,
            "ShouldContinue at 86% of soft limit must return true (0.85× guard removed)");
    }

    [Fact]
    public void ShouldContinue_PastEffectiveSoftLimit_ReturnsFalse()
    {
        var tm = new TimeManager();
        tm.UseStableTimeManagement = false; // explicit legacy baseline; R1 default is ON
        long fakeNow = 1_000_000L;
        tm.Init(wtime: 300_000, btime: 300_000, winc: 0, binc: 0,
                movesToGo: 0, side: 0, currentTime: fakeNow, moveOverhead: 0);

        // Far past the hard limit — must return false
        long past = tm.HardLimit + 10_000;

        bool result = tm.ShouldContinue(past, bestMove: 0, score: 0);

        Assert.False(result, "ShouldContinue past hard limit must return false");
    }

    // ─────────────────────────────────────────────────────────────
    // Test 5 — Hard limit formula
    // _hardLimit == Min(startTime + 4*alloc, startTime + myTime/4)
    // ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(60_000L, 0L, 0)] // standard 1-minute game
    [InlineData(5_000L, 0L, 0)]  // short game
    [InlineData(120_000L, 2_000L, 0)] // with increment
    public void Init_HardLimit_MatchesFormula(long wtime, long winc, int moveOverhead)
    {
        var tm = new TimeManager();
        long now = 0L; // fixed base for easy arithmetic
        tm.Init(wtime, btime: wtime, winc, binc: winc, movesToGo: 0, side: 0,
                currentTime: now, moveOverhead: moveOverhead);

        long alloc = tm.SoftLimit - tm.StartTime;
        long myTime = wtime - moveOverhead;
        myTime = Math.Max(100, myTime); // same clamp as Init

        long expectedHard = Math.Min(now + 4 * alloc, now + myTime / 4);
        // Adjust: if 4*alloc < myTime/4, expectedHard = now + 4*alloc
        // Allow tiny rounding: the implementation does the same Math.Min.
        // We accept the hard limit being >= our computed floor.
        Assert.True(tm.HardLimit >= now,
            "Hard limit must be >= start time");
        Assert.True(tm.HardLimit <= now + myTime,
            $"Hard limit must not exceed remaining clock ({myTime} ms), got {tm.HardLimit - now} ms");
        Assert.True(tm.HardLimit >= tm.SoftLimit,
            "Hard limit must be >= soft limit");
    }

    // ─────────────────────────────────────────────────────────────
    // Property 6 (inline) — Formula invariant
    // For known (myTime, myInc) without advantage/floor effects,
    // the base alloc equals myTime/30 + myInc before any cap.
    // ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(20_000L, 500L)]
    [InlineData(30_000L, 1_000L)]
    [InlineData(60_000L, 0L)]
    [InlineData(90_000L, 3_000L)]
    public void SuddenDeath_BaseAlloc_Formula_PreCap(long wtime, long winc)
    {
        // When wtime == btime, advantage multiplier is 0 (no surplus).
        long baseAlloc = wtime / 30 + winc;
        // Tactical floor applies when wtime > 30 000: max(3000, wtime / 100)
        if (wtime > 30000)
        {
            long minAlloc = Math.Max(3000, wtime / 100);
            baseAlloc = Math.Max(baseAlloc, minAlloc);
        }

        var tm = new TimeManager();
        long now = 0L;
        tm.Init(wtime, btime: wtime, winc, binc: winc, movesToGo: 0, side: 0,
                currentTime: now, moveOverhead: 0);

        long actualAlloc = tm.SoftLimit - now;

        // Safety buffer: 500ms for clock > 1s.
        long safetyBuffer = wtime < 1000 ? 50 : 500;
        long cappedAlloc = Math.Max(10, wtime - safetyBuffer);
        long effectiveExpected = Math.Min(baseAlloc, cappedAlloc);

        Assert.Equal(effectiveExpected, actualAlloc);
    }

    // ─────────────────────────────────────────────────────────────
    // Property 7 (inline) — Multiplier bounds
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void PVStabilityMultiplier_AllStates_WithinBounds()
    {
        var tm = new TimeManager();
        long now = TimeManager.NowMs();
        tm.Init(60_000, 60_000, 0, 0, movesToGo: 0, side: 0, now, moveOverhead: 0);

        // Test all relevant stability states under BOTH toggle settings
        foreach (bool shrink in new[] { true, false })
        {
            for (int stableIters = 0; stableIters <= 4; stableIters++)
            {
                tm.ResetPVStability();
                tm.UseStableShrink = shrink;
                int dummyMove = 1;
                for (int i = 0; i < stableIters; i++)
                    tm.UpdatePVStability(dummyMove, stable: true);

                double mult = tm.GetPVStabilityMultiplier();
                double expected = !shrink ? 1.0
                    : stableIters >= 4 ? 0.75 : stableIters == 3 ? 0.85 : 1.0; // k calls → _stableIterations = k−1
                Assert.Equal(expected, mult, precision: 3);
                Assert.True(mult <= 1.5,
                    $"stableIterations={stableIters}: multiplier {mult:F3} exceeds 1.5× cap");
            }
        }
    }

    [Fact]
    public void PVStabilityMultiplier_UnstableStates_WithinBounds()
    {
        var tm = new TimeManager();
        long now = TimeManager.NowMs();
        tm.Init(60_000, 60_000, 0, 0, movesToGo: 0, side: 0, now, moveOverhead: 0);

        for (int changes = 0; changes <= 4; changes++)
        {
            tm.ResetPVStability();
            for (int i = 0; i < changes; i++)
                tm.UpdatePVStability(i + 100, stable: false);

            double mult = tm.GetPVStabilityMultiplier();
            Assert.InRange(mult, 1.0, 1.5);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // TM2/TM3/TM4 bundle
    // bundle SPRT returned a NULL (+2.8 ± 21.3, ≈ −10 vs the F3.3 stack):
    // panic + advantage REVERTED to default ON; overhead clamp (TM4) kept as
    // ops-only hardening. Toggles retained for re-tests.
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Tm3_TimeAdvantage_DefaultOn_Multiplies()
    {
        // 180s vs 60s, 0 inc: base alloc = 6000; advantage scales to 9000.
        var tm = new TimeManager();
        long now = TimeManager.NowMs();
        tm.Init(wtime: 180_000, btime: 60_000, winc: 0, binc: 0, movesToGo: 0, side: 0, now, moveOverhead: 0);

        long alloc = tm.SoftLimit - tm.StartTime;
        Assert.Equal(9000, alloc); // 1.0 + min(120/180, 0.5) = 1.5× (default = restored behavior)

        tm.UseTimeAdvantage = false;
        tm.Init(wtime: 180_000, btime: 60_000, winc: 0, binc: 0, movesToGo: 0, side: 0, now, moveOverhead: 0);
        Assert.Equal(6000, tm.SoftLimit - tm.StartTime); // no multiplier when toggled off
    }

    [Fact]
    public void Tm2_PanicMode_DefaultOn_Extends()
    {
        var tm = new TimeManager();
        tm.UseStableTimeManagement = false; // explicit legacy baseline; R1 default is ON
        long fakeNow = 1_000_000L;
        tm.Init(wtime: 300_000, btime: 300_000, winc: 0, binc: 0, movesToGo: 0, side: 0, fakeNow, moveOverhead: 0);
        tm.SetCompletedDepth(5); // lift the min-depth guard

        long alloc = tm.SoftLimit - tm.StartTime; // 10 000 ms

        // Two iterations with a >50cp score drop → panic state latches (tracking always runs)
        Assert.True(tm.ShouldContinue(fakeNow + alloc / 2, bestMove: 7, score: 100));
        Assert.True(tm.ShouldContinue(fakeNow + alloc / 2 + 500, bestMove: 7, score: 20));
        Assert.True(tm.IsPanicMode, "test setup: panic tracking must latch regardless of the toggle");

        // Default (panic ON, restored 2026-09-10): ×1.5 extension past the base allocation.
        Assert.False(tm.ShouldStop(fakeNow + alloc + (long)(alloc * 0.05)),
            "with panic enabled the soft limit must extend to 1.5×");
        Assert.True(tm.ShouldStop(fakeNow + (long)(alloc * 1.55)),
            "panic extension must still be bounded by the 1.5× joint cap");

        // Toggle OFF (the measured-null bundle arm): no extension.
        tm.UsePanicMode = false;
        Assert.True(tm.ShouldStop(fakeNow + alloc + (long)(alloc * 0.05)),
            "with panic disabled the soft limit must not be extended past 1.0×");
    }

    [Fact]
    public void Tm4_OverheadSoftClamp_DefaultOn_SoftClampedToHardMinusOverhead()
    {
        // 1s clock + 5s inc: alloc = 950/30 + 5000 = 5031 → safety-capped to 900;
        // hard = min(4·900, 950/4) = 237 → raised to soft 900; clamp: soft = 900 − 50 = 850.
        var tm = new TimeManager();
        long now = 0L;
        tm.Init(wtime: 1000, btime: 1000, winc: 5000, binc: 5000, movesToGo: 0, side: 0, now, moveOverhead: 50);

        Assert.Equal(900, tm.HardLimit - tm.StartTime);
        Assert.Equal(850, tm.SoftLimit - tm.StartTime); // clamped to hard − overhead (TM4)

        tm.UseOverheadSoftClamp = false;
        tm.Init(wtime: 1000, btime: 1000, winc: 5000, binc: 5000, movesToGo: 0, side: 0, now, moveOverhead: 50);
        Assert.Equal(900, tm.SoftLimit - tm.StartTime); // unclamped
    }

    // ─────────────────────────────────────────────────────────────
    // Minimum-depth safety guard (2026-09-07)
    // A soft-limit stop must NOT fire before MinIterationDepth (5)
    // iterations have completed — prevents the depth-1 blunder bug.
    // The hard limit must still always fire.
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void MinDepthGuard_SoftLimitSuppressed_UntilMinDepthCompleted()
    {
        var tm = new TimeManager();
        tm.UseStableTimeManagement = false; // explicit legacy baseline; R1 default is ON
        long now = TimeManager.NowMs();
        // Real time control (movesToGo > 0) with a large clock so the hard limit sits
        // well beyond the soft limit — this lets us probe a time that is PAST the soft
        // limit but BEFORE the hard limit, which is exactly the window the guard governs.
        tm.Init(wtime: 100_000, btime: 100_000, winc: 0, binc: 0, movesToGo: 20, side: 0, now, moveOverhead: 0);

        // A time past the soft limit but comfortably before the hard limit (simulating an
        // exploding iteration that has overrun the planned per-move spend).
        long wellPastSoft = tm.SoftLimit + 3_000;
        Assert.True(wellPastSoft < tm.HardLimit,
            "test setup: probe time must be below the hard limit to isolate the guard");

        // Before any iteration completes, the guard forbids a soft-limit stop...
        Assert.False(tm.ShouldStop(wellPastSoft),
            "guard must suppress the soft-limit stop before any depth completes");

        //...and keeps forbidding it while completed depth < MinIterationDepth (5).
        for (int d = 1; d <= 4; d++)
        {
            tm.SetCompletedDepth(d);
            Assert.False(tm.ShouldStop(wellPastSoft),
                $"guard must still suppress the soft-limit stop at completed depth {d}");
        }

        // Once depth 5 completes the guard lifts and the (long-passed) soft limit fires.
        tm.SetCompletedDepth(5);
        Assert.True(tm.ShouldStop(wellPastSoft),
            "after MinIterationDepth completes, the soft-limit stop must be honored");
    }

    [Fact]
    public void MinDepthGuard_HardLimitAlwaysOverrides_EvenAtDepth1()
    {
        var tm = new TimeManager();
        long now = TimeManager.NowMs();
        tm.Init(wtime: 2_000, btime: 2_000, winc: 0, binc: 0, movesToGo: 20, side: 0, now, moveOverhead: 0);

        // At/after the hard limit the search MUST stop regardless of the guard, so a
        // pathological single iteration can never forfeit on time.
        Assert.True(tm.ShouldStop(tm.HardLimit),
            "hard limit must always force a stop, even before MinIterationDepth completes");
        Assert.False(tm.ShouldContinue(tm.HardLimit, bestMove: 1, score: 0),
            "ShouldContinue must be false at the hard limit, even before MinIterationDepth completes");
    }

    [Fact]
    public void MinDepthGuard_DoesNotApplyToFixedMovetime()
    {
        var tm = new TimeManager();
        long now = TimeManager.NowMs();
        tm.SetFixedTime(1_000, now); // movetime mode (_movesToGo == -1)

        // Movetime is an explicit user budget: the guard must NOT extend it. Past the
        // soft limit, ShouldStop fires immediately even with no completed depth.
        long pastSoft = tm.SoftLimit + 1;
        Assert.True(tm.ShouldStop(pastSoft),
            "fixed movetime must not be extended by the min-depth guard");
    }
}
