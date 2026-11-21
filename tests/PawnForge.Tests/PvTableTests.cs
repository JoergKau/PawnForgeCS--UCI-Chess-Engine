// ═══════════════════════════════════════════════════════════════════════════════
// PvTable · explicit triangular PV table — unit tests
// ═══════════════════════════════════════════════════════════════════════════════

using PawnForge.Core.Board;
using PawnForge.Core.Search;
using Xunit;

namespace PawnForge.Tests;

/// <summary>
/// Unit tests for the explicit triangular Principal Variation table
/// (<see cref="PvTable"/>).
///
/// <para>
/// The table replaces the TT-walk PV reconstruction: every search node
/// initializes its row (<see cref="PvTable.InitPly"/>), and every exact PV
/// improvement is recorded (<see cref="PvTable.MergeMove"/>), so the root can
/// read the finished principal variation in one shot (<see cref="PvTable.GetPv"/>).
/// </para>
///
/// <para>
/// These tests verify the triangular-merge semantics directly on the class — no
/// search needed: row P always holds [its move, then the child row at P+1...];
/// re-merging at the same ply replaces the previous line; and the longest
/// possible line is bounded by MAX_PLY.
/// </para>
/// </summary>
public class PvTableTests
{
    // Small helper to build distinct moves (from -> to, no flags needed for the table itself).
    private static Move Mv(int from, int to) => new(from, to);

    [Fact]
    public void GetPv_ReturnsEmpty_WhenNothingMerged()
    {
        // A fresh table has no root line yet.
        var pv = new PvTable();
        Assert.True(pv.GetPv().IsEmpty);
    }

    [Fact]
    public void GetPv_ReturnsEmpty_AfterRootReinitialized()
    {
        // InitPly(0) must wipe a previously merged root line (the aspiration
        // re-search calls Negamax(ply 0) again and must start from a clean row).
        var pv = new PvTable();
        pv.MergeMove(1, Mv(10, 11));
        pv.MergeMove(0, Mv(0, 1));
        Assert.Equal(2, pv.GetPv().Length);

        pv.InitPly(0);
        Assert.True(pv.GetPv().IsEmpty);
    }

    [Fact]
    public void MergeMove_BuildsTriangularLine_FromChildRows()
    {
        // Merge at ply 2, then ply 1, then ply 0. Each MergeMove concatenates the
        // current move with the already-built child row:
        // ply 2: [20->21]
        // ply 1: [10->11, 20->21]
        // ply 0: [0->1, 10->11, 20->21]
        var pv = new PvTable();
        pv.InitPly(2);
        pv.MergeMove(2, Mv(20, 21));
        pv.InitPly(1);
        pv.MergeMove(1, Mv(10, 11));
        pv.InitPly(0);
        pv.MergeMove(0, Mv(0, 1));

        Assert.Equal(
            new[] { Mv(0, 1), Mv(10, 11), Mv(20, 21) },
            pv.GetPv().ToArray());
    }

    [Fact]
    public void MergeMove_SiblingImprovement_ReplacesPreviousLine()
    {
        // Two sibling lines at ply 1: the second improvement at ply 1 must fully
        // replace the first, and a later root merge must reflect ONLY the newest
        // child row (no stale tail from the old sibling).
        var pv = new PvTable();

        // First sibling: ply1 = [10->11, 20->21]
        pv.MergeMove(2, Mv(20, 21));
        pv.InitPly(1);
        pv.MergeMove(1, Mv(10, 11));

        // Second sibling at the same ply: ply1 = [12->13, 22->23] only.
        pv.InitPly(2);
        pv.MergeMove(2, Mv(22, 23));
        pv.InitPly(1);
        pv.MergeMove(1, Mv(12, 13));

        // Root merge picks up the second sibling's line.
        pv.InitPly(0);
        pv.MergeMove(0, Mv(2, 3));

        Assert.Equal(
            new[] { Mv(2, 3), Mv(12, 13), Mv(22, 23) },
            pv.GetPv().ToArray());
    }

    [Fact]
    public void MergeMove_FullDepthChain_IsBoundedByMaxPly()
    {
        // Merging from the deepest usable ply down to the root must build the
        // longest possible line without overflowing the flat array: exactly
        // MAX_PLY - 1 moves (ply MAX_PLY-1 can never merge — it has no child row).
        var pv = new PvTable();
        for (int ply = PvTable.MAX_PLY - 2; ply >= 0; ply--)
        {
            pv.MergeMove(ply, Mv(ply, ply + 1));
        }

        var line = pv.GetPv();
        Assert.Equal(PvTable.MAX_PLY - 1, line.Length);
        Assert.Equal(Mv(0, 1), line[0]);
        Assert.Equal(Mv(PvTable.MAX_PLY - 2, PvTable.MAX_PLY - 1), line[^1]);
    }

    [Fact]
    public void Indexer_RoundTripsCells_AcrossPlies()
    {
        // Direct cell access (used for debugging/inspection) must read back what
        // it was given, independent of the merge bookkeeping.
        var pv = new PvTable();
        pv[3, 5] = Mv(30, 35);
        pv[0, 0] = Mv(0, 9);

        Assert.Equal(Mv(30, 35), pv[3, 5]);
        Assert.Equal(Mv(0, 9), pv[0, 0]);
        Assert.True(pv[3, 6].IsNone, "untouched cells must hold Move.None");
    }
}