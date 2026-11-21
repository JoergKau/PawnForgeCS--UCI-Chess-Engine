using System;
using System.Runtime.CompilerServices;

namespace PawnForge.Core.Board
{
    /// <summary>
    /// Attack detection: "is square X attacked by side Y?" plus the pin and
    /// checker infrastructure.
    ///
    /// TECHNIQUE (attack queries): for the target square, generate the
    /// attacks a DEFENDER would have from there (pawn-capture symmetry trick,
    /// knight/king tables, magic slider rays against the full occupancy) and
    /// intersect with the attacker's piece bitboards. One query answers for
    /// all piece types.
    ///
    /// BetweenTable[64,64] precomputes, for every ordered square pair, the
    /// squares strictly BETWEEN them - used by the pin detection (a slider
    /// x-raying the king with exactly one own blocker = pin) and SEE.
    ///
    /// The cached Pinned/Checkers bitboards live on Position (refreshed per
    /// make/unmake by Position.UpdateKingThreats, which uses this class).
    /// </summary>
    public static class AttackDetection
    {
        // Precomputed attack bitboards for sliding pieces
        private static readonly ulong[] KnightAttacks = new ulong[64];
        private static readonly ulong[] KingAttacks = new ulong[64];
        private static readonly ulong[,] PawnAttacks = new ulong[2, 64]; // [color, square]
        public static readonly ulong[,] BetweenTable = new ulong[64, 64];

        static AttackDetection()
        {
            ulong FileA = 0x0101010101010101UL;
            ulong FileH = 0x8080808080808080UL;

            for (int sq = 0; sq < 64; sq++)
            {
                ulong b = 1UL << sq;

                // White Pawns (Attacks: NW +7, NE +9)
                if ((b & ~FileA) != 0) PawnAttacks[0, sq] |= (b << 7);
                if ((b & ~FileH) != 0) PawnAttacks[0, sq] |= (b << 9);

                // Black Pawns (Attacks: SE -7, SW -9)
                if ((b & ~FileA) != 0) PawnAttacks[1, sq] |= (b >> 9);
                if ((b & ~FileH) != 0) PawnAttacks[1, sq] |= (b >> 7);

                // Init King/Knight from MagicBitboards
                KingAttacks[sq] = MagicBitboards.GetKingAttacks(sq);
                KnightAttacks[sq] = MagicBitboards.GetKnightAttacks(sq);

                // Precompute BetweenTable
                for (int sq2 = 0; sq2 < 64; sq2++)
                {
                    if (sq == sq2) continue;
                    BetweenTable[sq, sq2] = ComputeBetweenInternal(sq, sq2);
                }
            }
        }

        private static ulong ComputeBetweenInternal(int a, int b)
        {
            int fa = a % 8, ra = a / 8;
            int fb = b % 8, rb = b / 8;
            int df = fb - fa, dr = rb - ra;

            int step = 0;
            if (dr == 0 && df != 0) step = df > 0 ? 1 : -1;      // same rank
            else if (df == 0 && dr != 0) step = dr > 0 ? 8 : -8; // same file
            else if (System.Math.Abs(df) == System.Math.Abs(dr)) // diagonal
            {
                if (df > 0 && dr > 0) step = 9;
                else if (df > 0 && dr < 0) step = -7;
                else if (df < 0 && dr > 0) step = 7;
                else step = -9;
            }
            else return 0UL;

            ulong bb = 0UL;
            int t = a + step;
            while (t != b)
            {
                bb |= (1UL << t);
                t += step;
                if (t < 0 || t >= 64) break;
            }
            return bb;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GetKnightAttacks(int sq)
        {
            return KnightAttacks[sq];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GetKingAttacks(int sq)
        {
            return KingAttacks[sq];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GetPawnAttacks(int sq, int defendingSide) // defendingSide: 0=white defending, 1=black defending
        {
            // attacking side is opposite of defending side. 
            // If defendingSide=0 (White), attackers are Black (1).
            // PawnAttacks[1, sq] stores where Black pawns attack FROM square sq? No.
            // PawnAttacks[color, sq] typically stores attacks FROM sq by color.
            // We want to know if 'sq' is attacked BY 'attacker'.
            
            // Let's verify Position.cs implementation:
            // ulong pawnAttacks = GetPawnAttacks(sq, 1 - attackerSide);
            // if ((pawnAttacks & pieces[attackerSide][0]) != 0) return true;
            
            // If we are checking if White King at 'sq' is attacked by Black Pawn.
            // We need to know if any Black Pawn is on a square that attacks 'sq'.
            // The squares that attacks 'sq' are the same as where a White Pawn at 'sq' would attack!
            // Wait, Black Pawn captures diagonally down. White Pawn captures diagonally up.
            // A Black Pawn at sq+7 attacks sq (if simplified).
            // Actually, usually PawnAttacks table is: "Attacks FROM sq".
            // So if we want "Attacks TO sq", we look at where a pawn of OPPOSITE color at 'sq' would attack.
            // Because capture is symmetric geometry?
            // White Pawn at E4 attacks D5, F5.
            // Black Pawn at D5 attacks E4. Black Pawn at F5 attacks E4.
            // So "Attacks TO E4 by Black" == "Attacks FROM E4 by White".
            
            // Position.cs used: GetPawnAttacks(sq, 1 - attackerSide)
            // If attacker is Black (1), we call GetPawnAttacks(sq, 0).
            // 0 is White. So we get squares attacked BY White FROM sq.
            // And identifying if Black pawns are there. Yes, that's correct symmetry.
            
            return PawnAttacks[defendingSide, sq]; 
        }

        /// <summary>
        /// Sliding-attack dispatch: PEXT when the runtime
        /// benchmark selected it (BMI2 + faster), magic otherwise. The two implementations are
        /// bit-identical (the PEXT tables are built from the magic results), so this choice
        /// changes NPS only. See <see cref="PextBitboards"/>.
        /// </summary>
        private static readonly bool UsePext = PextBitboards.Ready && PextBitboards.UsePext;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GetBishopAttacks(int sq, ulong blockers)
        {
            return UsePext
                ? PextBitboards.BishopAttacks(sq, blockers)
                : MagicBitboards.BishopAttacks(sq, blockers);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GetRookAttacks(int sq, ulong blockers)
        {
            return UsePext
                ? PextBitboards.RookAttacks(sq, blockers)
                : MagicBitboards.RookAttacks(sq, blockers);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GetQueenAttacks(int sq, ulong blockers)
        {
            return GetRookAttacks(sq, blockers) | GetBishopAttacks(sq, blockers);
        }

        public static bool IsSquareAttackedBy(Position pos, int sq, int attackerSide)
        {
            ulong[][] pieces = pos.Pieces;
            ulong allOccupancy = pos.AllOccupancy;

            // pawns
            // Pass the "defender" side (current square perspective) to get capture symmetry
            // If we want to know if 'sq' is attacked by 'attackerSide', we check where 
            // a pawn of the OTHER side at 'sq' would attack.
            ulong pawnAttacks = GetPawnAttacks(sq, 1 - attackerSide); 
            if ((pawnAttacks & pieces[attackerSide][0]) != 0) return true;

            // knights
            ulong knightAttacks = GetKnightAttacks(sq);
            if ((knightAttacks & pieces[attackerSide][1]) != 0) return true;

            // king
            ulong kingAttacks = GetKingAttacks(sq);
            if ((kingAttacks & pieces[attackerSide][5]) != 0) return true;

            // bishops and queens (diagonal)
            ulong bishopAttacks = GetBishopAttacks(sq, allOccupancy);
            if ((bishopAttacks & (pieces[attackerSide][2] | pieces[attackerSide][4])) != 0) return true;

            // rooks and queens (orthogonal)
            ulong rookAttacks = GetRookAttacks(sq, allOccupancy);
            if ((rookAttacks & (pieces[attackerSide][3] | pieces[attackerSide][4])) != 0) return true;

            return false;
        }
    }
}
