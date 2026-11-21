using System;

namespace PawnForge.Core.Search;

/// <summary>
/// Static helpers updating the search heuristics tables (all per-thread,
/// living in ThreadData): killer moves, butterfly history, capture history
/// and continuation history.
///
/// HIST1 SCHEME : every update goes
/// through the GRAVITY formula h += u − h·|u|/MAX (the reference's bound-keeper,
///native constants), replacing the old plain
/// `+=` with a ±200,000 hard clamp. Bonuses/maluses are LINEAR in depth
/// (reference-style), not depth². The v1 LMR path consumes the combined quiet
/// score continuously via <see cref="LmrHistoryTerm1024"/> — sub-ply
/// granularity, bounded ±2.35 plies.
///
/// AGING IS KEPT (×7/8 per iteration, d ≥ 4): the initial plan to remove it
/// was reverted the same day — PF's history PERSISTS across searches and the
/// per-iteration aging is the saturation regulator (without it, tables ride
/// the gravity cap ±8033 within one long search and POISON the next one:
/// observed as a mate-in-4 battery flip to cp 1581 in the unit suite).
/// Gravity + aging has steady state h* = 7u·MAX/(MAX+7u) ≈ ±4108 (u = 1202) —
/// exactly the reference-magnitude band the LMR divisor (3419) is tuned for.
/// the reference himself ships without aging, but his per-move searches are short;
/// PF's persistent-history design needs the regulator.
/// </summary>
public static class SearchHeuristics
{
    // ── HIST1 per-table scale constants (reference-tuned starting values) ──
    public const int ButterflyMax = 8033;
    public const int ContMax = 8003;
    public const int CaptureMax = 8488;

    public const int ButterflyBonusScale = 207, ButterflyBonusMax = 1202;
    public const int ButterflyMalusScale = 200, ButterflyMalusMax = 1255;
    public const int ContBonusScale = 204, ContBonusMax = 1229;
    public const int ContMalusScale = 205, ContMalusMax = 1067;
    public const int CaptureBonusScale = 192, CaptureBonusMax = 1250;
    public const int CaptureMalusScale = 211, CaptureMalusMax = 1185;

    // ── LMR history term (HIST1): continuous, clamped, in 1024ths ──
    public const int LmrHistoryClamp = 8033;   // = ButterflyMax; combined quiet score is clamped here
    public const int LmrQuietHistoryDiv = 3419; // tunable weight, in 1024ths
    public const int LmrNoisyHistoryDiv = 3257; // tunable weight, in 1024ths (unused in v1: LMR quiets only)
    public const int LmrUnit = 1024;

    /// <summary>
    /// Gravity update: h += u − h·|u|/MAX. For |h| &lt; MAX and |u| ≤ MAX the
    /// result stays within (−MAX, MAX) — no explicit clamp needed (and none
    /// allowed: the asymptote IS the bound-keeper).
    /// </summary>
    public static int Gravity(int h, int update, int max)
        => h + update - (int)((long)h * Math.Abs(update) / max);

    /// <summary>
    /// LMR history term (HIST1): clamped continuous contribution in 1024ths of
    /// a ply. Max magnitude 8033/3258·1024 ≈ 2524 (= 2.47 plies) — bounded by
    /// construction, unlike the old unclamped `history/8192` whole-ply term
    /// that could reach ±24 plies at the old table cap.
    ///
    /// `precise` selects the multiply-before-divide
    /// form `(long)h · 1024 / divisor` instead of `h / divisor · 1024`. The legacy form
    /// truncates every |h| &lt; divisor to ZERO (so most ordinary history scores contribute
    /// nothing); the precise form keeps the sub-ply fraction, 1024× finer modulation. Only
    /// the v1 LMR call site uses the flag; the v2 block keeps the reference's own divide-first shape.
    /// </summary>
    public static int LmrHistoryTerm1024(int combinedHist, bool quiet, bool precise = false)
    {
        int h = combinedHist;
        if (h > LmrHistoryClamp) h = LmrHistoryClamp;
        else if (h < -LmrHistoryClamp) h = -LmrHistoryClamp;
        int divisor = quiet ? LmrQuietHistoryDiv : LmrNoisyHistoryDiv;
        return precise
            ? (int)((long)h * LmrUnit / divisor)
            : h / divisor * LmrUnit;
    }

    /// <summary>
    /// Updates the killer moves table for a given ply.
    /// Killers are quiet moves that caused a beta cutoff and are tried early in sibling nodes.
    /// </summary>
    public static void UpdateKillers(Move[][] killers, Move m, int ply)
    {
        if (ply >= 256) return;
        var k = killers[ply];
        if (!m.Equals(k[0]))
        {
            // Shift killers down
            k[3] = k[2];
            k[2] = k[1];
            k[1] = k[0];
            k[0] = m;
        }
    }

    /// <summary>
    /// HIST1 butterfly history (gravity-bounded ±8033): linear bonus for the
    /// cutoff move, linear malus for the failed-low quiets.
    /// </summary>
    public static void UpdateHistory(ThreadData td, int side, Move m, int depth, ReadOnlySpan<Move> badMoves)
    {
        int u = Math.Min(ButterflyBonusScale * depth, ButterflyBonusMax);
        ref int h = ref td.ButterflyHistory[side][m.From][m.To];
        h = Gravity(h, u, ButterflyMax);

        int mu = Math.Min(ButterflyMalusScale * depth, ButterflyMalusMax);
        for (int i = 0; i < badMoves.Length; i++)
        {
            Move bad = badMoves[i];
            ref int hb = ref td.ButterflyHistory[side][bad.From][bad.To];
            hb = Gravity(hb, -mu, ButterflyMax);
        }
    }

    /// <summary>
    /// HIST1 continuation history (gravity-bounded ±8003): linear bonus for a
    /// cutoff move, linear malus for a failed-low quiet (bonus=false). The
    /// malus is NEW — the old scheme was bonus-only, which let the tables
    /// saturate positively and lose all discriminating power.
    /// </summary>
    public static void UpdateContHistory(int[][] table, Move m, int depth, bool bonus = true)
    {
        int u = bonus
            ? Math.Min(ContBonusScale * depth, ContBonusMax)
            : -Math.Min(ContMalusScale * depth, ContMalusMax);
        ref int h = ref table[m.From][m.To];
        h = Gravity(h, u, ContMax);
    }

    /// <summary>
    /// HIST1 capture history (gravity-bounded ±8488): linear bonus for a
    /// cutoff capture. Kept bonus-only (PF tracks no searched-captures list;
    /// capture ordering is SEE/MVV-dominated and the asymmetric pull is
    /// bounded by gravity) — deviation from the design doc noted in HIST1.
    /// </summary>
    public static void UpdateCaptureHistory(ThreadData td, int pieceIdx, int to, int victimIdx, int depth)
    {
        int u = Math.Min(CaptureBonusScale * depth, CaptureBonusMax);
        ref int h = ref td.CaptureHistory[pieceIdx][to][victimIdx];
        h = Gravity(h, u, CaptureMax);
    }

    /// <summary>
    /// HIST1: ages all history tables by ×7/8 (12.5 % decay) per completed
    /// iteration (d ≥ 4). KEPT after the aging-removal experiment poisoned
    /// cross-search state (see class doc): gravity bounds each cell, aging
    /// keeps it away FROM the bound — steady state ≈ ±4108, the the reference
    /// magnitude the LMR divisor assumes.
    /// </summary>
    public static void AgeHistory(ThreadData td)
    {
        // Age Butterfly History
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 64; j++)
                for (int k = 0; k < 64; k++)
                    td.ButterflyHistory[i][j][k] = (td.ButterflyHistory[i][j][k] * 7) / 8;

        // Age Capture History
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 64; j++)
                for (int k = 0; k < 6; k++)
                    td.CaptureHistory[i][j][k] = (td.CaptureHistory[i][j][k] * 7) / 8;

        // Age Continuation History
        AgeContinuationHistory(td.ContinuationHistory1Ply);
        AgeContinuationHistory(td.ContinuationHistory2Ply);
        AgeContinuationHistory(td.ContinuationHistory4Ply);
        AgeContinuationHistory(td.ContinuationHistory6Ply);
    }

    /// <summary>
    /// Ages a continuation history table by reducing all values by 12.5%.
    /// </summary>
    private static void AgeContinuationHistory(int[][][][] table)
    {
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 64; j++)
                for (int k = 0; k < 64; k++)
                    for (int l = 0; l < 64; l++)
                        table[i][j][k][l] = (table[i][j][k][l] * 7) / 8;
    }
    /// <summary>
    /// Converts a piece to its index in history tables (0-5 for P,N,B,R,Q,K).
    /// </summary>
    public static int PieceToIndex(Piece p)
    {
        return p switch
        {
            Piece.WP or Piece.BP => 0,
            Piece.WN or Piece.BN => 1,
            Piece.WB or Piece.BB => 2,
            Piece.WR or Piece.BR => 3,
            Piece.WQ or Piece.BQ => 4,
            Piece.WK or Piece.BK => 5,
            _ => -1
        };
    }
}
