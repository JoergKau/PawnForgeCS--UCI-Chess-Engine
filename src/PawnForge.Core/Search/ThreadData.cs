namespace PawnForge.Core.Search
{
    /// <summary>
    /// Per-thread search state: everything that must be thread-local in the
    /// Lazy SMP design. One instance per Search instance.
    ///
    /// CONTENTS:
    /// Metrics - false-sharing-padded node counter (plain long,
    /// no atomics; aggregation only at info time)
    /// ButterflyHistory - [side][from][to] quiet-move ordering scores
    /// CaptureHistory - [attacker][to][victim] capture ordering scores
    /// Continuation 1/2/4/6-ply - [side][prevTo][from][to] move sequences
    /// CounterMoves - [from][to] best-reply heuristic
    /// CorrectionHistory - [side][zobrist &amp; 16383] eval adjustments (write-side)
    /// Reduction/Eval/MoveCount stacks - per-ply search bookkeeping
    /// Move/ScoredMove buffers - pre-allocated per-ply picker scratch space
    ///
    /// LIFETIME: Reset clears counters, countermoves and stacks between
    /// searches; the history tables PERSIST (gravity-bounded + per-iteration
    /// ×7/8 aging, HIST1 2026-09-10) so knowledge accumulates across moves.
    /// </summary>
    public class ThreadData
    {
        /// <summary>Index of this thread (0 = main thread: info emission, TT generation, time checks).</summary>
        public int ThreadId { get; }

        // ThreadMetrics: plain long fields with 64-byte padding to isolate the
        // node counter from other threads' cache lines (no atomics in the hot
        // path - each thread owns its counter, aggregation happens at info time).
        public readonly ThreadMetrics Metrics = new ThreadMetrics();

        /// <summary>P0 instrumentation counters (only updated when
        /// <see cref="SearchDiagnostics.Enabled"/> is on).</summary>
        public readonly SearchStats Stats = new();

        /// <summary>This thread's node count (no atomics - see ThreadMetrics).</summary>
        public long Nodes
        {
            get => Metrics.Nodes;
            set => Metrics.Nodes = value;
        }

        /// <summary>
        /// Increment the node counter WITHOUT atomic operations:
        /// Each thread has its own _nodes field with cache line padding to prevent false sharing.
        /// Aggregation only happens in UciHandler when reporting info lines.
        /// This is FASTER (no atomics in hot path) and SAFER (no race conditions).
        /// </summary>
        public void IncrementNodes()
        {
            Metrics.IncrementNodes();
        }
        
        /// <summary>
        /// Prevent compiler optimization of the padding fields
        /// </summary>
        /// <summary>
        /// Prevent compiler optimization of padding fields (delegates to Metrics)
        /// </summary>
        public long PreventOptimization() => Metrics.PreventOptimization();

        // Move ordering history tables
        // Butterfly history: [side][from][to]
        public readonly int[][][] ButterflyHistory;

        // Capture History: [pieceType][to_square][victim_pieceType] 3D model
        public readonly int[][][] CaptureHistory;

        // Per-ply stacks for the hindsight reductions
        private const int MAX_PLY = 128;
        public readonly int[] ReductionStack = new int[MAX_PLY];
        public readonly int[] EvalStack = new int[MAX_PLY];
        public readonly int[] MoveCountStack = new int[MAX_PLY];

        // LMR v2 per-ply stacks (the reference engine):
        //
        // PvDistanceStack[ply] — "plies since the last full-window (PV) node" (the reference:
        // `curr.pvDistance = pvNode ? 0 : prev.pvDistance + 1`). The LMR
        // PV-distance term reduces late moves harder the further the current node sits from
        // the principal variation. Set at Negamax node entry for every visit.
        //
        // FailHighCountStack[ply] — count of beta cutoffs recorded at this stack slot
        // (the reference: `curr.failHighCount++` on fail-high,; reset two plies
        // ahead at node entry. Because a cutoff breaks the move loop, the
        // counter accumulates ACROSS visits that reuse the same stack slot (sibling subtrees
        // and iterative-deepening re-visits) until the grandparent node resets it — a
        // statistical "cutoff-hot zone" detector. The LMR fail-high-count term reduces late
        // moves harder when the parent slot has failed high more than twice. Mirrors the reference's
        // exact reset/increment/read pattern.
        public readonly int[] PvDistanceStack = new int[MAX_PLY];
        public readonly int[] FailHighCountStack = new int[MAX_PLY];

        /// <summary>
        /// NMP verification seal (the reference's
        /// <c>td.nmpPly</c>,/368-374). 0 = NMP may fire everywhere.
        /// While a deep null-move verification search runs (UseVerifiedNmp, depth > 14),
        /// this is set to <c>(3·(depth−1−R)/4) + ply</c>: NMP is then SUPPRESSED at any
        /// node whose ply is below the seal, so the verification subtree cannot itself
        /// launch nested verifications — each verification chain-limits itself, exactly
        /// like the reference's. A per-thread plain int (no stack — the reference uses a single slot
        /// too; the toggle's fail-high branch only ever sets it while a nested node
        /// would read it as "already sealed" and take the direct-return path).
        /// </summary>
        public int NmpSealPly;

        /// <summary>
        /// Nodes spent per ROOT move, accumulated across the
        /// whole search (`[from, to]`). Feeds the stable-TM node-fraction term
        /// (`bestMoveNodes / nodes`), the reference's `nodesPerMove`. Written only at the root move
        /// loop (thread 0's tree is the one the time manager uses); cleared by <see cref="Reset"/>.
        /// </summary>
        public readonly long[,] NodesPerMove = new long[64, 64];

        // ── per-thread EVAL CACHE (default OFF) ────────────
        /// <summary>Cache size hint in MB, set by the owning <see cref="Search"/> from the TT
        /// size (`clamp(hash/2, 8, 1024)`); the cache itself is allocated lazily.</summary>
        public int EvalCacheSizeMb;

        /// <summary>The per-thread static-eval cache, or null until the first cached eval.
        /// Never shared between threads (see <see cref="EvalCache"/>).</summary>
        public EvalCache? EvalCache;

        /// <summary>Number of eval-cache hits in this thread's lifetime (diagnostic + the
        /// wiring test's proof that the cache is actually used).</summary>
        public long EvalCacheHits;

        /// <summary>Lazily allocates (once) and returns this thread's eval cache.</summary>
        public EvalCache EnsureEvalCache()
            => EvalCache ??= new EvalCache(EvalCacheSizeMb);


        // Pre-allocated Move Buffers for Search (Gen, Bad) and ScoredMove Buffers (Caps, Quiets)
        // [Ply][BufferIndex][Size]
        // MoveBuffers: 0=Gen, 1=Bad
        // ScoredMoveBuffers: 0=Caps, 1=Quiets
        public readonly Move[][][] MoveBuffers;
        public readonly ScoredMove[][][] ScoredMoveBuffers;

        /// <summary>Creates the thread state and pre-allocates all tables and buffers.</summary>
        public ThreadData(int id)
        {
            ThreadId = id;
            Nodes = 0;
            
            // Initialize history
            ButterflyHistory = new int[2][][];
            for (int i = 0; i < 2; i++)
            {
                ButterflyHistory[i] = new int[64][];
                for (int j = 0; j < 64; j++)
                {
                    ButterflyHistory[i][j] = new int[64];
                }
            }

            // Initialize move buffers (Optimization to avoid heap allocs during search)
            MoveBuffers = new Move[MAX_PLY][][];
            ScoredMoveBuffers = new ScoredMove[MAX_PLY][][];
            for (int p = 0; p < MAX_PLY; p++)
            {
                MoveBuffers[p] = new Move[2][];
                MoveBuffers[p][0] = new Move[256];
                MoveBuffers[p][1] = new Move[256];

                ScoredMoveBuffers[p] = new ScoredMove[2][];
                ScoredMoveBuffers[p][0] = new ScoredMove[256];
                ScoredMoveBuffers[p][1] = new ScoredMove[256];
            }

            // Initialize capture history [6 attacker types][64 squares][6 victim types]
            CaptureHistory = new int[6][][];
            for (int i = 0; i < 6; i++)
            {
                CaptureHistory[i] = new int[64][];
                for (int j = 0; j < 64; j++)
                {
                    CaptureHistory[i][j] = new int[6];
                }
            }

            CounterMoves = new Move[64, 64];
            InitContinuationHistory(out ContinuationHistory1Ply);
            InitContinuationHistory(out ContinuationHistory2Ply);
            InitContinuationHistory(out ContinuationHistory4Ply);
            InitContinuationHistory(out ContinuationHistory6Ply);

            CorrectionHistory = new int[2][];
            for (int i = 0; i < 2; i++) CorrectionHistory[i] = new int[16384];

            // reference-style 4-term correction history (
            //: pawn-key term + per-color non-pawn-key terms +
            // countermove (piece-to) term. Each table stores the reference's depth-weighted
            // running average of eval-vs-search diffs as a short, clamped ±2048
            // (CorrectionHistoryTable.MAX = SCALE * 32).
            CorrectionPawn = new short[2][];
            CorrectionNonPawn = new short[2][][];
            CorrectionCont = new short[2][][];
            for (int i = 0; i < 2; i++)
            {
                CorrectionPawn[i] = new short[16384];
                CorrectionNonPawn[i] = new short[2][];
                for (int c = 0; c < 2; c++) CorrectionNonPawn[i][c] = new short[16384];
                CorrectionCont[i] = new short[6][];
                for (int p = 0; p < 6; p++) CorrectionCont[i][p] = new short[64];
            }
        }

        /// <summary>
        /// Clears the node counter, countermove table and per-ply stacks.
        /// The history tables deliberately PERSIST between searches (gravity
        /// keeps them bounded, per-iteration aging keeps them away from the
        /// bound) - knowledge accumulates across moves.
        /// </summary>
        public void Reset()
        {
            // CRITICAL FIX #4: Simple reset - no flush needed (each thread owns its _nodes)
            Nodes = 0;
            // Clear history? Usually history persists between searches but maybe age it?
            // For now, keep it simple.
            // ButterflyHistory is usually persisted but aged.
            // We can clear CounterMoves though.
            Array.Clear(CounterMoves, 0, CounterMoves.Length);
            
            // Clear per-ply stacks
            Array.Clear(ReductionStack, 0, ReductionStack.Length);
            Array.Clear(EvalStack, 0, EvalStack.Length);
            Array.Clear(MoveCountStack, 0, MoveCountStack.Length);
            Array.Clear(PvDistanceStack, 0, PvDistanceStack.Length);
            Array.Clear(FailHighCountStack, 0, FailHighCountStack.Length);
            NmpSealPly = 0; // no seal between searches
            Array.Clear(NodesPerMove, 0, NodesPerMove.Length);
        }

        // Counter Moves: [PrevFrom][PrevTo] -> ResponseMove
        public readonly Move[,] CounterMoves;

        // Continuation History: [Side][PrevTo][From][To]
        // 1-ply history
        public readonly int[][][][] ContinuationHistory1Ply;

        // 2-ply history
        public readonly int[][][][] ContinuationHistory2Ply;

        // 4-ply history
        public readonly int[][][][] ContinuationHistory4Ply;

        // 6-ply history
        public readonly int[][][][] ContinuationHistory6Ply;

        // Correction History: [Side][PawnKey & Mask] -> Score adjustment
        // Simple 16K entries per side (legacy pawn-only additive design; used when
        // Search.UseRefCorrectionHistory is OFF)
        public readonly int[][] CorrectionHistory;

        // reference-style correction history (
        // pawnCorrHistTable / nonPawnCorrHistTables /
        // countermoveCorrHistTable, all CorrectionHistoryTable descendants):
        // CorrectionPawn [stm][pawnKey & 16383] (HashCorrectionTable)
        // CorrectionNonPawn [keyColor][stm][nonPawnKey & 16383] (2 × HashCorrectionTable —
        // the white/black NON-PAWN material keys)
        // CorrectionCont [stm][prevPieceType][prevMove.To] (PieceToCorrectionTable)
        // All values are the reference's depth-weighted running averages (short, clamped ±2048).
        public readonly short[][] CorrectionPawn;
        public readonly short[][][] CorrectionNonPawn;
        public readonly short[][][] CorrectionCont;

        private void InitContinuationHistory(out int[][][][] table)
        {
             // Dense History: [Side][PrevTo][From][To]
             table = new int[2][][][];
             for(int s=0; s<2; s++)
             {
                 table[s] = new int[64][][];
                 for(int sq=0; sq<64; sq++)
                 {
                     table[s][sq] = new int[64][];
                     for(int f=0; f<64; f++) 
                     {
                         table[s][sq][f] = new int[64];
                     }
                 }
             }
        }

    }
}
