// ═══════════════════════════════════════════════════════════════════════════════
// SEARCH · explicit triangular Principal Variation (PV) table (A)
// ═══════════════════════════════════════════════════════════════════════════════

using System.Runtime.CompilerServices;

namespace PawnForge.Core.Search;

/// <summary>
/// An explicit, allocation-free triangular Principal Variation (PV) table.
///
/// <para>
/// PROBLEM THIS SOLVES (A):
/// PawnForge used to reconstruct the PV by walking the transposition table
/// (<c>GetPvFromTT</c>) — probing the TT ply-by-ply, re-matching each packed move
/// against freshly generated legal moves, and make/unmaking the board along the
/// way. Under aggressive pruning and deep search (depth 20+), non-PV lines churn
/// the TT. If an entry along the PV line is overwritten or aged out, the
/// reconstructed PV truncates or swaps candidate moves. This produced the
/// chronic <c>MOVE-CHANGING</c> / move-instability warnings on some positions,
/// which poisons time management and triggers unnecessary aspiration re-searches.
/// </para>
///
/// <para>
/// FIX: track the principal variation directly on the search stack, exactly like
/// the reference engines do. The design reference's PV table (56 lines of pure,
/// allocation-free array operations) is the model. Every search node
/// initializes its own row (<see cref="InitPly"/>); whenever a move raises alpha
/// at that node WITHOUT failing high (an exact, PV-defining improvement), it is
/// merged into the table (<see cref="MergeMove"/>), concatenating the move with
/// the already-computed PV of the child node. The root then reads the finished
/// line in one shot via <see cref="GetPv"/> — no TT walk, no board mutation, no
/// make/unmake overhead.
/// </para>
///
/// <para>
/// MEMORY LAYOUT: a single flat <see cref="Move"/> array sized MAX_PLY × MAX_PLY
/// ("triangular" because ply P only ever uses rows P..MAX_PLY), plus a per-ply
/// length array. <see cref="MergeMove"/> at ply P copies the child row (P+1) into
/// slots [P, 1..] so row P always holds the complete line P..MAX_PLY. Pure
/// value-array bookkeeping — it never influences any search decision, so search
/// scores and node counts are bit-identical whether the table is populated or
/// not. Toggled by <c>Search.UsePvTable</c> (default on); when disabled, the
/// legacy TT-walk extraction remains available.
/// </para>
/// </summary>
public sealed class PvTable
{
    /// <summary>
    /// Maximum supported search depth in plies. Must match ThreadData's MAX_PLY
    /// (128) so the table can never be indexed beyond the search's own stacks.
    /// </summary>
    internal const int MAX_PLY = 128;

    // Flat move storage: row `ply` occupies [ply*MAX_PLY .. ply*MAX_PLY + MAX_PLY).
    private readonly Move[] _moves = new Move[MAX_PLY * MAX_PLY];

    // Length of the PV line currently stored in each row.
    private readonly int[] _length = new int[MAX_PLY];

    /// <summary>Direct cell access for inspection/debugging (tested by the unit suite).</summary>
    public Move this[int ply, int n]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _moves[GetIndex(ply, n)];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _moves[GetIndex(ply, n)] = value;
    }

    /// <summary>
    /// Resets the PV row for <paramref name="ply"/> to empty. Must be called on
    /// ENTRY to every search node at that ply (before any move is searched) so a
    /// previous visit to the same ply — a sibling line, a null-move probe, a
    /// singular-extension verification, a TT cutoff — can never leave a stale PV
    /// behind for the current node's <see cref="MergeMove"/> to pick up.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void InitPly(int ply)
    {
        _length[ply] = 0;
    }

    /// <summary>
    /// Records <paramref name="move"/> as an exact PV improvement at
    /// <paramref name="ply"/>: the new line is [move, child line at ply+1...].
    /// The child must already have populated its own row (a full-window child
    /// search always completes before the parent merges), so this is a single
    /// store plus one contiguous <see cref="Array.Copy"/> of the child's line.
    /// </summary>
    /// <remarks>
    /// Calling convention (mirrors the design reference's search): invoke ONLY when a
    /// move's score is above alpha AND below beta — i.e. an exact improvement,
    /// never a fail-high/beta-cutoff (cutoffs have no PV-defining best line).
    /// </remarks>
    public void MergeMove(int ply, Move move)
    {
        _length[ply] = _length[ply + 1] + 1;
        _moves[GetIndex(ply, 0)] = move;
        Array.Copy(_moves, GetIndex(ply + 1, 0), _moves, GetIndex(ply, 1), _length[ply + 1]);
    }

    /// <summary>
    /// The complete principal variation from the root (ply 0): the move sequence
    /// reported as <c>info ... pv ...</c> after an iteration completes. An empty
    /// span means the root found no exact improvement (e.g. search was stopped
    /// before any move raised alpha).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Move> GetPv()
    {
        return new ReadOnlySpan<Move>(_moves, 0, _length[0]);
    }

    /// <summary>Flat-array offset for the cell (ply, n).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetIndex(int ply, int n)
    {
        return ply * MAX_PLY + n;
    }
}