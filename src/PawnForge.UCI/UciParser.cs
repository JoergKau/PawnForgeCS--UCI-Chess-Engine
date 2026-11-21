using System;
using System.Text;
using PawnForge.Core.Board;

namespace PawnForge.UCI
{
    /// <summary>
    /// Static helper class for parsing and formatting UCI commands and moves.
    /// </summary>
    public static class UciParser
    {
        /// <summary>
        /// Parses the `go` parameter set: depth, nodes, movetime, wtime/btime
        /// with increments, movestogo, infinite, perft and searchmoves.
        /// Defaults to depth 6 when no limiting parameter is given.
        /// </summary>
        public static SearchLimits ParseGoCommand(string command)
        {
            var limits = new SearchLimits();
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            for (int i = 1; i < parts.Length; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "depth":
                        if (i + 1 < parts.Length && int.TryParse(parts[++i], out int depth))
                            limits.Depth = depth;
                        break;
                        
                    case "nodes":
                        if (i + 1 < parts.Length && long.TryParse(parts[++i], out long nodes))
                            limits.Nodes = nodes;
                        break;
                        
                    case "movetime":
                        if (i + 1 < parts.Length && int.TryParse(parts[++i], out int movetime))
                            limits.MoveTime = movetime;
                        break;
                        
                    case "wtime":
                        if (i + 1 < parts.Length && int.TryParse(parts[++i], out int wtime))
                            limits.WhiteTime = wtime;
                        break;
                        
                    case "btime":
                        if (i + 1 < parts.Length && int.TryParse(parts[++i], out int btime))
                            limits.BlackTime = btime;
                        break;
                        
                    case "winc":
                        if (i + 1 < parts.Length && int.TryParse(parts[++i], out int winc))
                            limits.WhiteIncrement = winc;
                        break;
                        
                    case "binc":
                        if (i + 1 < parts.Length && int.TryParse(parts[++i], out int binc))
                            limits.BlackIncrement = binc;
                        break;
                        
                    case "movestogo":
                        if (i + 1 < parts.Length && int.TryParse(parts[++i], out int movestogo))
                            limits.MovesToGo = movestogo;
                        break;
                        
                    case "mate":
                        if (i + 1 < parts.Length && int.TryParse(parts[++i], out int mate))
                            limits.Mate = mate;
                        break;

                    case "searchmoves":
                        // Consume all following move tokens (e2e4 e7e8q ...)
                        while (i + 1 < parts.Length && IsMoveToken(parts[i + 1]))
                            limits.SearchMoves.Add(parts[++i]);
                        break;

                    case "infinite":
                        limits.Infinite = true;
                        break;

                    case "ponder":
                        // `go ponder`: think on the opponent's move. The
                        // search runs until `ponderhit` / `stop` and must not
                        // emit a bestmove on its own.
                        limits.Ponder = true;
                        break;
                        
                    case "perft":
                        if (i + 1 < parts.Length && int.TryParse(parts[++i], out int perftDepth))
                            limits.PerftDepth = perftDepth;
                        break;
                }
            }
            
            // Default depth if no limits specified
            if (limits.Depth == 0 && !limits.HasTimeControl && !limits.Infinite
                && limits.Nodes == 0 && limits.PerftDepth == 0 && limits.Mate == 0)
            {
                limits.Depth = 6;
            }
            
            return limits;
        }

        private static bool IsMoveToken(string token)
        {
            // UCI move: 4-5 chars, "a1"-style from/to, optional promotion char.
            return token.Length is 4 or 5
                && token[0] >= 'a' && token[0] <= 'h'
                && token[1] >= '1' && token[1] <= '8'
                && token[2] >= 'a' && token[2] <= 'h'
                && token[3] >= '1' && token[3] <= '8';
        }

        public static Move ParseMove(Position position, string moveStr)
        {
            // UCI move format: e2e4, e7e8q (for promotion)
            if (string.IsNullOrWhiteSpace(moveStr) || moveStr.Length < 4)
                return Move.None;
            
            int from = SquareFromString(moveStr.Substring(0, 2));
            int to = SquareFromString(moveStr.Substring(2, 2));
            
            if (from < 0 || to < 0)
                return Move.None;
            
            // Check for promotion
            int promotion = 0;
            if (moveStr.Length == 5)
            {
                promotion = moveStr[4] switch
                {
                    'q' => (int)Piece.WQ, // Use white pieces, color doesn't matter for promotion type
                    'r' => (int)Piece.WR,
                    'b' => (int)Piece.WB,
                    'n' => (int)Piece.WN,
                    _ => 0
                };
            }
            
            // Find matching legal move
            foreach (var move in position.GenerateMoves())
            {
                if (move.From == from && move.To == to)
                {
                    if (promotion > 0)
                    {
                        // Compare by piece TYPE (index), not exact value
                        // MoveGenerator uses color-specific pieces (e.g., BQ=11 for black)
                        // but parser always uses white pieces (e.g., WQ=5)
                        int parsedType = ((Piece)promotion).ToIndex();
                        int moveType = ((Piece)move.Promoted).ToIndex();
                        if (move.Promoted == 0 || parsedType != moveType)
                            continue;
                    }
                    
                    return move;
                }
            }
            
            return Move.None;
        }
        
        public static string FormatMove(Move move)
        {
            if (move.IsNone)
                return "0000";
            
            var sb = new StringBuilder();
            sb.Append(SquareToString(move.From));
            sb.Append(SquareToString(move.To));
            
            if (move.Promoted != 0)
            {
                // Extract piece type from promoted piece (remove color)
                var promoPiece = (Piece)move.Promoted;
                char promoChar = promoPiece switch
                {
                    Piece.WQ or Piece.BQ => 'q',
                    Piece.WR or Piece.BR => 'r',
                    Piece.WB or Piece.BB => 'b',
                    Piece.WN or Piece.BN => 'n',
                    _ => 'q'
                };
                sb.Append(promoChar);
            }
            
            return sb.ToString();
        }
        
        public static int SquareFromString(string square)
        {
            if (square.Length != 2)
                return -1;
            
            char file = square[0];
            char rank = square[1];
            
            if (file < 'a' || file > 'h' || rank < '1' || rank > '8')
                return -1;
            
            return (rank - '1') * 8 + (file - 'a');
        }
        
        public static string SquareToString(int square)
        {
            int file = square % 8;
            int rank = square / 8;
            return $"{(char)('a' + file)}{(char)('1' + rank)}";
        }
    }
}
