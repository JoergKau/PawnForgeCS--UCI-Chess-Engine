namespace PawnForge.Core.Board;

/// <summary>
/// Named square constants (A1 = 0 … H8 = 63, little-endian rank-file
/// mapping: square = rank * 8 + file) plus the castling destination
/// squares used by the move generator.
/// </summary>
public static class Square
{
    // Square constants
    public const int A1 = 0, B1 = 1, C1 = 2, D1 = 3, E1 = 4, F1 = 5, G1 = 6, H1 = 7;
    public const int A2 = 8, B2 = 9, C2 = 10, D2 = 11, E2 = 12, F2 = 13, G2 = 14, H2 = 15;
    public const int A3 = 16, B3 = 17, C3 = 18, D3 = 19, E3 = 20, F3 = 21, G3 = 22, H3 = 23;
    public const int A4 = 24, B4 = 25, C4 = 26, D4 = 27, E4 = 28, F4 = 29, G4 = 30, H4 = 31;
    public const int A5 = 32, B5 = 33, C5 = 34, D5 = 35, E5 = 36, F5 = 37, G5 = 38, H5 = 39;
    public const int A6 = 40, B6 = 41, C6 = 42, D6 = 43, E6 = 44, F6 = 45, G6 = 46, H6 = 47;
    public const int A7 = 48, B7 = 49, C7 = 50, D7 = 51, E7 = 52, F7 = 53, G7 = 54, H7 = 55;
    public const int A8 = 56, B8 = 57, C8 = 58, D8 = 59, E8 = 60, F8 = 61, G8 = 62, H8 = 63;

    private static readonly string[] Ranks = { "1","2","3","4","5","6","7","8" };
    private static readonly char[] Files = { 'a','b','c','d','e','f','g','h' };

    public static int FromFileRank(int file, int rank) => rank * 8 + file;

    public static int FromAlgebraic(string s)
    {
        if (s.Length != 2) return -1;
        int file = s[0] - 'a';
        int rank = s[1] - '1';
        return FromFileRank(file, rank);
    }

    public static string ToString(int sq)
    {
        int file = sq % 8;
        int rank = sq / 8;
        return $"{Files[file]}{rank+1}";
    }
}