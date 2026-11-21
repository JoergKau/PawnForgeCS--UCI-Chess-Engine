namespace PawnForge.Core.Board;

/// <summary>How a move alters the position beyond a simple piece displacement.</summary>
public enum MoveType
{
    Normal,
    Castle,
    EnPassant
}

/// <summary>
/// A chess move as an immutable record: from/to square indices plus flags.
/// NOT bit-packed (16 bytes in memory) - only the TranspositionTable packs
/// moves into 16 bits for storage (From | To&lt;&lt;6 | Promoted&lt;&lt;12).
///
/// Promoted stores a COLOR-CODED Piece constant (WQ=5 / BQ=11); for black
/// promotions the white constant is stored and translated during make.
/// Equality is by value; Move.None (default) marks "no move".
/// </summary>
/// <param name="From">Source square index 0-63 (A1 = 0).</param>
/// <param name="To">Target square index 0-63.</param>
/// <param name="Promoted">Colored Piece constant for promotions, else 0.</param>
/// <param name="IsEnPassant">True for en-passant captures (target differs from the captured pawn's square).</param>
/// <param name="IsCastle">True for castling (king moves two files; the rook move is implicit).</param>
public readonly record struct Move(int From, int To, int Promoted = 0, bool IsEnPassant = false, bool IsCastle = false)
{
    /// <summary>The invalid "no move" sentinel (From == To).</summary>
    public static Move None => default;
    public bool IsNone => From == To;

    public MoveType MoveType => IsCastle ? MoveType.Castle : (IsEnPassant ? MoveType.EnPassant : MoveType.Normal);
    public Piece PromotionPiece => Promoted != 0 ? (Piece)Promoted : Piece.Empty;

    public override string ToString()
    {
        var s = $"{Square.ToString(From)}{Square.ToString(To)}";
        if (Promoted != 0)
        {
            // UCI promotion format: q(queen), r(rook), b(bishop), n(knight)
            // Promoted stores actual Piece enum values:
            // White: WN=2, WB=3, WR=4, WQ=5
            // Black: BN=8, BB=9, BR=10, BQ=11
            s += (Piece)Promoted switch
            {
                Piece.WQ or Piece.BQ => "q",
                Piece.WR or Piece.BR => "r",
                Piece.WB or Piece.BB => "b",
                Piece.WN or Piece.BN => "n",
                _ => "q"  // Fallback to queen (should not happen)
            };
        }
        return s;
    }
}