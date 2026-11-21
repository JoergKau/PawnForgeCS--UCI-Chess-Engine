namespace PawnForge.Core.Board;

/// <summary>
/// The 13 chess pieces: color-coded constants (WP..WK = 1-6 white,
/// BP..BK = 7-12 black) plus Empty = 0. Extension methods provide the
/// color-free piece-type index (ToIndex, 0=Pawn..5=King), FEN characters
/// and color tests.
/// </summary>
public enum Piece : int
{
    Empty = 0, 
    WP = 1, WN = 2, WB = 3, WR = 4, WQ = 5, WK = 6, 
    BP = 7, BN = 8, BB = 9, BR = 10, BQ = 11, BK = 12 
}

public static class PieceExtensions
{
    public static int ToIndex(this Piece p)
    {
        return p switch
        {
            Piece.WP or Piece.BP => 0,
            Piece.WN or Piece.BN => 1,
            Piece.WB or Piece.BB => 2,
            Piece.WR or Piece.BR => 3,
            Piece.WQ or Piece.BQ => 4,
            Piece.WK or Piece.BK => 5,
            _ => 0
        };
    }

    public static bool IsWhite(this Piece p) => p >= Piece.WP && p <= Piece.WK;
    
    public static char ToChar(this Piece p) => p switch
    {
        Piece.WP => 'P', Piece.WN => 'N', Piece.WB => 'B', Piece.WR => 'R', Piece.WQ => 'Q', Piece.WK => 'K',
        Piece.BP => 'p', Piece.BN => 'n', Piece.BB => 'b', Piece.BR => 'r', Piece.BQ => 'q', Piece.BK => 'k',
        _ => '.'
    };
    
    public static Piece FromChar(char c) => c switch
    {
        'P' => Piece.WP, 'N' => Piece.WN, 'B' => Piece.WB, 'R' => Piece.WR, 'Q' => Piece.WQ, 'K' => Piece.WK,
        'p' => Piece.BP, 'n' => Piece.BN, 'b' => Piece.BB, 'r' => Piece.BR, 'q' => Piece.BQ, 'k' => Piece.BK,
        _ => Piece.Empty
    };
}
