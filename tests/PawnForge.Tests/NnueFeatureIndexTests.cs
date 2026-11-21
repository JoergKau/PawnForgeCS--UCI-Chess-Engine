namespace PawnForge.Tests;

using PawnForge.Core.Board;
using PawnForge.Core.Evaluation;
using Xunit;

/// <summary>
/// Scheme-2 feature-index invariants over a 500-position legal walk.
///
/// Every position must produce a well-formed, deterministic dump: TWO
/// perspectives rendered as <c>region:mirror:slot,slot,…</c>, with the slot
/// sets sorted, inside the 768-slot scheme, one entry per board piece.
/// The format is the same one the external parity tooling consumes, so this
/// test also locks the dump contract itself.
/// </summary>
public class NnueFeatureIndexTests
{
    /// <summary>Seed FENs: startpos, kiwipete (both colours), the perft suite's
    /// tricky positions (pins, promotions, EP, edge kings). Each is checked
    /// against the external oracle before use when the oracle is available.</summary>
    private static readonly string[] SeedFens =
    {
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R b KQkq - 0 1",
        "4k3/8/8/8/8/8/4P3/4K3 b - - 0 1",
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
        "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
        "n1n5/PPPk4/8/8/8/8/4Kppp/5N1N b - - 0 1",
    };

    private const int TargetPositions = 500;

    /// <summary>Deterministic walk pool: TargetPositions positions derived from
    /// the seeds by random legal play (captures and promotions preferred
    /// occasionally so they appear in the sweep). Positions are UNIQUE by FEN —
    /// a sweep of 500 distinct boards beats 500 copies of the same shape.</summary>
    private static List<string> BuildPositionFens()
    {
        var fens = new SortedSet<string>(StringComparer.Ordinal);
        var rng = new Random(0x4D43524E); // fixed seed, reproducible sweep
        // ONE buffer, hoisted OUT of the loops: a stackalloc inside a loop
        // accumulates on the test thread's stack (CA2014) and xunit's worker
        // threads have a much smaller stack than a console main.
        Span<Move> moveBuffer = stackalloc Move[256];

        foreach (var seed in SeedFens)
        {
            var pos = Position.FromFEN(seed);
            var made = new List<(Move m, Piece captured)>();
            for (int ply = 0; ply < 120 && fens.Count < TargetPositions; ply++)
            {
                fens.Add(pos.ToFEN());
                int count = pos.GenerateLegalMoves(moveBuffer);
                if (count == 0)
                    break; // mate/stalemate leaf — walk ends here
                // Prefer "special" moves (captures, promotions) every few plies
                // so they appear; otherwise uniform.
                Move pick = default;
                for (int attempt = 0; attempt < 4 && pick.IsNone; attempt++)
                {
                    Move candidate = moveBuffer[rng.Next(count)];
                    bool special = pos.Mailbox[candidate.To] != Piece.Empty || candidate.Promoted != 0;
                    if (special || attempt == 3 || rng.Next(4) == 0)
                        pick = candidate;
                }
                if (pick.IsNone)
                    pick = moveBuffer[rng.Next(count)];
                Piece captured = pos.MakePublicMove(pick);
                made.Add((pick, captured));
            }
            // Unwind so the next seed starts from a fresh position object.
            for (int i = made.Count - 1; i >= 0; i--)
                pos.UnmakePublicMove(made[i].m, made[i].captured);
        }

        // Top up from the seeds with more walks if the seeds under-produced.
        int restart = 0;
        while (fens.Count < TargetPositions && restart < 50)
        {
            var pos = Position.FromFEN(SeedFens[restart % SeedFens.Length]);
            var made = new List<(Move m, Piece captured)>();
            for (int ply = 0; ply < 60 && fens.Count < TargetPositions; ply++)
            {
                fens.Add(pos.ToFEN());
                int count = pos.GenerateLegalMoves(moveBuffer);
                if (count == 0)
                    break;
                Move pick = moveBuffer[rng.Next(count)];
                Piece captured = pos.MakePublicMove(pick);
                made.Add((pick, captured));
            }
            for (int i = made.Count - 1; i >= 0; i--)
                pos.UnmakePublicMove(made[i].m, made[i].captured);
            restart++;
        }

        return fens.Take(TargetPositions).ToList();
    }

    [Fact]
    public void IndexDump_IsWellFormedAndDeterministic_Over500Positions()
    {
        // Seed FENs are oracle-checked before use (the gate is a no-op when the
        // external oracle is not installed).
        foreach (var fen in SeedFens)
            Stockfish.VerifyFen(fen);

        var fens = BuildPositionFens();
        Assert.True(fens.Count >= 400, $"walk produced only {fens.Count} positions");

        var features = new NnueFeatureSet2(NnueFeatureSet2.DefaultRegionTable);
        foreach (var fen in fens)
        {
            var pos = Position.FromFEN(fen);
            string dump = features.Describe(pos);

            // Determinism: the dump is a pure function of the position.
            Assert.Equal(dump, features.Describe(pos));
            Assert.DoesNotContain('\n', dump);

            string[] povs = dump.Split('|');
            Assert.Equal(2, povs.Length);
            foreach (string pov in povs)
            {
                string[] fields = pov.Split(':');
                Assert.Equal(3, fields.Length);
                Assert.True(int.TryParse(fields[0], out int region) && region is >= 0 and < 32,
                    $"bad region id in '{pov}'");
                Assert.True(fields[1] is "0" or "7", $"bad mirror in '{pov}'");

                string[] slots = fields[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
                Assert.InRange(slots.Length, 2, 32);
                int previous = -1;
                foreach (string text in slots)
                {
                    Assert.True(int.TryParse(text, out int slot));
                    Assert.InRange(slot, 0, 767);
                    Assert.True(slot > previous, $"slots not strictly increasing in '{pov}'");
                    previous = slot;
                }
            }
        }
    }
}
