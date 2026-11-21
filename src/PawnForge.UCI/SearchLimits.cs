using System;

namespace PawnForge.UCI
{
    /// <summary>
    /// Holds every `go` parameter of one search request: time controls,
    /// depth/nodes/mate targets, infinite/perft modes and the searchmoves
    /// restriction list. Produced by UciParser, consumed by the orchestrator.
    /// </summary>
    public class SearchLimits
    {
        // Time control
        public int WhiteTime { get; set; }        // milliseconds
        public int BlackTime { get; set; }        // milliseconds
        public int WhiteIncrement { get; set; }   // milliseconds
        public int BlackIncrement { get; set; }   // milliseconds
        public int MovesToGo { get; set; }
        public int MoveTime { get; set; }         // milliseconds for fixed time per move
        
        // Depth and node limits
        public int Depth { get; set; }
        public long Nodes { get; set; }
        public int Mate { get; set; }                 // go mate N (0 = off)
        public List<string> SearchMoves { get; } = new();  // go searchmoves e2e4 ...
        
        // Search modes
        public bool Infinite { get; set; }
        public int PerftDepth { get; set; }       // If > 0, run perft instead of search

        /// <summary>
        /// True when `go ponder` was received: the engine is thinking on the
        /// OPPONENT's move (from the current position, opponent to move). In
        /// this mode the search keeps running until `ponderhit` or `stop` and
        /// must NOT emit a `bestmove` by itself - the GUI decides when the
        /// pondered move is played (`ponderhit`) or not (`stop`).
        ///
        /// Reference: UCI protocol specification (ponder section),
        /// http://wbec-ridderkerk.nl/html/UCIProtocol.html
        /// </summary>
        public bool Ponder { get; set; }
        
        public DateTime StartTime { get; set; }
        
        public SearchLimits()
        {
            StartTime = DateTime.UtcNow;
        }
        
        public bool IsPerft => PerftDepth > 0;
        
        /// <summary>
        /// Returns true if this is a time-controlled search (not depth/nodes/infinite).
        /// </summary>
        public bool HasTimeControl => MoveTime > 0 || WhiteTime > 0 || BlackTime > 0;
    }
}
