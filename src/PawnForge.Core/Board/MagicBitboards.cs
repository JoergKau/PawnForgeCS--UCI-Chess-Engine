using System;
using System.Numerics;

namespace PawnForge.Core
{
    /// <summary>
    /// Plain (non-fancy) magic bitboards for sliding-piece attack generation.
    ///
    /// TECHNIQUE: for each square, a MAGIC multiplier hashes the relevant
    /// occupancy bits into a small lookup table index:
    /// attacks = table[square][(occupancy &amp; mask) * magic &gt;&gt; shift]
    /// The tables are built once at static initialization by enumerating all
    /// blocker subsets of the square's edge-masked rays.
    ///
    /// TABLE SIZES: rook ≈ 0.73 MiB (96,256 entries), bishop ≈ 41 KiB
    /// (5,248 entries) - both fit in L2 cache.
    ///
    /// NOTE: the class lives in namespace PawnForge.Core (not .Board) - a
    /// historical quirk; do not "fix" without checking all usages.
    /// </summary>
    public static class MagicBitboards
    {
        // Magic numbers and shifts
        private static readonly ulong[] RookMagics = new ulong[64];
        private static readonly ulong[] BishopMagics = new ulong[64];
        private static readonly int[] RookShifts = new int[64];
        private static readonly int[] BishopShifts = new int[64];
        
        private static readonly ulong[] RookMasks = new ulong[64];
        private static readonly ulong[] BishopMasks = new ulong[64];

        private static readonly ulong[][] RookAttackTable = new ulong[64][];
        private static readonly ulong[][] BishopAttackTable = new ulong[64][];
        private static readonly ulong[] KnightAttacksTable = new ulong[64];
        private static readonly ulong[] KingAttacksTable = new ulong[64];

        static MagicBitboards()
        {
            InitMagicNumbers();
            
            for (int sq = 0; sq < 64; sq++)
            {
                KnightAttacksTable[sq] = GenerateKnightAttacksInternal(sq);
                KingAttacksTable[sq] = GenerateKingAttacksInternal(sq);
                
                RookMasks[sq] = GenerateRookMask(sq);
                BishopMasks[sq] = GenerateBishopMask(sq);

                int rookBits = 64 - RookShifts[sq];
                int rookSize = 1 << rookBits;
                RookAttackTable[sq] = new ulong[rookSize];
                
                int bishopBits = 64 - BishopShifts[sq];
                int bishopSize = 1 << bishopBits;
                BishopAttackTable[sq] = new ulong[bishopSize];
            }
            
            // Build attack tables using magic indexing
            for (int sq = 0; sq < 64; sq++)
            {
                // Rook attacks
                ulong rookMask = RookMasks[sq];
                int rookBits = BitOperations.PopCount(rookMask);
                int rookPerms = 1 << rookBits;
                
                for (int i = 0; i < rookPerms; i++)
                {
                    ulong occ = IndexToOccupancy(i, rookMask);
                    ulong attacks = ComputeRookAttacksBrute(sq, occ);
                    int idx = MagicIndex(occ, RookMagics[sq], RookShifts[sq]);
                    RookAttackTable[sq][idx] = attacks;
                }
                
                // Bishop attacks
                ulong bishopMask = BishopMasks[sq];
                int bishopBits = BitOperations.PopCount(bishopMask);
                int bishopPerms = 1 << bishopBits;
                
                for (int i = 0; i < bishopPerms; i++)
                {
                    ulong occ = IndexToOccupancy(i, bishopMask);
                    ulong attacks = ComputeBishopAttacksBrute(sq, occ);
                    int idx = MagicIndex(occ, BishopMagics[sq], BishopShifts[sq]);
                    BishopAttackTable[sq][idx] = attacks;
                }
            }
        }

        // Magic bitboard lookup (multiply and shift)
        public static ulong RookAttacks(int sq, ulong occ)
        {
            occ &= RookMasks[sq];
            occ *= RookMagics[sq];
            occ >>= RookShifts[sq];
            return RookAttackTable[sq][(int)occ];
        }

        public static ulong BishopAttacks(int sq, ulong occ)
        {
            occ &= BishopMasks[sq];
            occ *= BishopMagics[sq];
            occ >>= BishopShifts[sq];
            return BishopAttackTable[sq][(int)occ];
        }
        
        private static int MagicIndex(ulong occ, ulong magic, int shift)
        {
            return (int)((occ * magic) >> shift);
        }
        
        private static ulong IndexToOccupancy(int index, ulong mask)
        {
            ulong occ = 0UL;
            int bits = BitOperations.PopCount(mask);
            for (int i = 0; i < bits; i++)
            {
                int bitPos = BitOperations.TrailingZeroCount(mask);
                if ((index & (1 << i)) != 0)
                    occ |= (1UL << bitPos);
                mask &= mask - 1; // Clear LSB
            }
            return occ;
        }

        private static ulong GenerateRookMask(int sq)
        {
            int r = sq / 8;
            int f = sq % 8;
            ulong mask = 0UL;
            // Up
            for (int rr = r + 1; rr <= 6; rr++) mask |= 1UL << (rr * 8 + f);
            // Down
            for (int rr = r - 1; rr >= 1; rr--) mask |= 1UL << (rr * 8 + f);
            // Right
            for (int ff = f + 1; ff <= 6; ff++) mask |= 1UL << (r * 8 + ff);
            // Left
            for (int ff = f - 1; ff >= 1; ff--) mask |= 1UL << (r * 8 + ff);
            return mask;
        }

        private static ulong GenerateBishopMask(int sq)
        {
            int r = sq / 8;
            int f = sq % 8;
            ulong mask = 0UL;
            // NE
            for (int rr = r + 1, ff = f + 1; rr <= 6 && ff <= 6; rr++, ff++) mask |= 1UL << (rr * 8 + ff);
            // NW
            for (int rr = r + 1, ff = f - 1; rr <= 6 && ff >= 1; rr++, ff--) mask |= 1UL << (rr * 8 + ff);
            // SE
            for (int rr = r - 1, ff = f + 1; rr >= 1 && ff <= 6; rr--, ff++) mask |= 1UL << (rr * 8 + ff);
            // SW
            for (int rr = r - 1, ff = f - 1; rr >= 1 && ff >= 1; rr--, ff--) mask |= 1UL << (rr * 8 + ff);
            return mask;
        }

        private static void InitMagicNumbers()
        {
            // Magic numbers per square
            ulong[] rookMagics = new ulong[] {
                0x0080001020400080L, 0x0040001000200040L, 0x0080081000200080L, 0x0080040800100080L,
                0x0080020400080080L, 0x0080010200040080L, 0x0080008001000200L, 0x0080002040800100L,
                0x0000800020400080L, 0x0000400020005000L, 0x0000801000200080L, 0x0000800800100080L,
                0x0000800400080080L, 0x0000800200040080L, 0x0000800100020080L, 0x0000800040800100L,
                0x0000208000400080L, 0x0000404000201000L, 0x0000808010002000L, 0x0000808008001000L,
                0x0000808004000800L, 0x0000808002000400L, 0x0000010100020004L, 0x0000020000408104L,
                0x0000208080004000L, 0x0000200040005000L, 0x0000100080200080L, 0x0000080080100080L,
                0x0000040080080080L, 0x0000020080040080L, 0x0000010080800200L, 0x0000800080004100L,
                0x0000204000800080L, 0x0000200040401000L, 0x0000100080802000L, 0x0000080080801000L,
                0x0000040080800800L, 0x0000020080800400L, 0x0000020001010004L, 0x0000800040800100L,
                0x0000204000808000L, 0x0000200040008080L, 0x0000100020008080L, 0x0000080010008080L,
                0x0000040008008080L, 0x0000020004008080L, 0x0000010002008080L, 0x0000004081020004L,
                0x0000204000800080L, 0x0000200040008080L, 0x0000100020008080L, 0x0000080010008080L,
                0x0000040008008080L, 0x0000020004008080L, 0x0000800100020080L, 0x0000800041000080L,
                0x00FFFCDDFCED714AL, 0x007FFCDDFCED714AL, 0x003FFFCDFFD88096L, 0x0000040810002101L,
                0x0001000204080011L, 0x0001000204000801L, 0x0001000082000401L, 0x0001FFFAABFAD1A2L
            };

            ulong[] bishopMagics = new ulong[] {
                0x0002020202020200L, 0x0002020202020000L, 0x0004010202000000L, 0x0004040080000000L,
                0x0001104000000000L, 0x0000821040000000L, 0x0000410410400000L, 0x0000104104104000L,
                0x0000040404040400L, 0x0000020202020200L, 0x0000040102020000L, 0x0000040400800000L,
                0x0000011040000000L, 0x0000008210400000L, 0x0000004104104000L, 0x0000002082082000L,
                0x0004000808080800L, 0x0002000404040400L, 0x0001000202020200L, 0x0000800802004000L,
                0x0000800400A00000L, 0x0000200100884000L, 0x0000400082082000L, 0x0000200041041000L,
                0x0002080010101000L, 0x0001040008080800L, 0x0000208004010400L, 0x0000404004010200L,
                0x0000840000802000L, 0x0000404002011000L, 0x0000808001041000L, 0x0000404000820800L,
                0x0001041000202000L, 0x0000820800101000L, 0x0000104400080800L, 0x0000020080080080L,
                0x0000404040040100L, 0x0000808100020100L, 0x0001010100020800L, 0x0000808080010400L,
                0x0000820820004000L, 0x0000410410002000L, 0x0000082088001000L, 0x0000002011000800L,
                0x0000080100400400L, 0x0001010101000200L, 0x0002020202000400L, 0x0001010101000200L,
                0x0000410410400000L, 0x0000208208200000L, 0x0000002084100000L, 0x0000000020880000L,
                0x0000001002020000L, 0x0000040408020000L, 0x0004040404040000L, 0x0002020202020000L,
                0x0000104104104000L, 0x0000002082082000L, 0x0000000020841000L, 0x0000000000208800L,
                0x0000000010020200L, 0x0000000404080200L, 0x0000040404040400L, 0x0002020202020200L
            };

            int[] rookShifts = new int[] {
                52, 53, 53, 53, 53, 53, 53, 52,
                53, 54, 54, 54, 54, 54, 54, 53,
                53, 54, 54, 54, 54, 54, 54, 53,
                53, 54, 54, 54, 54, 54, 54, 53,
                53, 54, 54, 54, 54, 54, 54, 53,
                53, 54, 54, 54, 54, 54, 54, 53,
                53, 54, 54, 54, 54, 54, 54, 53,
                53, 54, 54, 53, 53, 53, 53, 53
            };

            int[] bishopShifts = new int[] {
                58, 59, 59, 59, 59, 59, 59, 58,
                59, 59, 59, 59, 59, 59, 59, 59,
                59, 59, 57, 57, 57, 57, 59, 59,
                59, 59, 57, 55, 55, 57, 59, 59,
                59, 59, 57, 55, 55, 57, 59, 59,
                59, 59, 57, 57, 57, 57, 59, 59,
                59, 59, 59, 59, 59, 59, 59, 59,
                58, 59, 59, 59, 59, 59, 59, 58
            };

            System.Array.Copy(rookMagics, RookMagics, 64);
            System.Array.Copy(bishopMagics, BishopMagics, 64);
            System.Array.Copy(rookShifts, RookShifts, 64);
            System.Array.Copy(bishopShifts, BishopShifts, 64);
        }

        private static ulong ComputeRookAttacksBrute(int sq, ulong occ)
        {
            ulong attacks = 0UL;
            int r = sq / 8;
            int f = sq % 8;
            // Up
            for (int rr = r + 1; rr <= 7; rr++)
            {
                int s = rr * 8 + f;
                attacks |= 1UL << s;
                if (((occ >> s) & 1UL) != 0UL) break;
            }
            // Down
            for (int rr = r - 1; rr >= 0; rr--)
            {
                int s = rr * 8 + f;
                attacks |= 1UL << s;
                if (((occ >> s) & 1UL) != 0UL) break;
            }
            // Right
            for (int ff = f + 1; ff <= 7; ff++)
            {
                int s = r * 8 + ff;
                attacks |= 1UL << s;
                if (((occ >> s) & 1UL) != 0UL) break;
            }
            // Left
            for (int ff = f - 1; ff >= 0; ff--)
            {
                int s = r * 8 + ff;
                attacks |= 1UL << s;
                if (((occ >> s) & 1UL) != 0UL) break;
            }
            return attacks;
        }

        private static ulong ComputeBishopAttacksBrute(int sq, ulong occ)
        {
            ulong attacks = 0UL;
            int r = sq / 8;
            int f = sq % 8;
            // NE
            for (int rr = r + 1, ff = f + 1; rr <= 7 && ff <= 7; rr++, ff++)
            {
                int s = rr * 8 + ff;
                attacks |= 1UL << s;
                if (((occ >> s) & 1UL) != 0UL) break;
            }
            // NW
            for (int rr = r + 1, ff = f - 1; rr <= 7 && ff >= 0; rr++, ff--)
            {
                int s = rr * 8 + ff;
                attacks |= 1UL << s;
                if (((occ >> s) & 1UL) != 0UL) break;
            }
            // SE
            for (int rr = r - 1, ff = f + 1; rr >= 0 && ff <= 7; rr--, ff++)
            {
                int s = rr * 8 + ff;
                attacks |= 1UL << s;
                if (((occ >> s) & 1UL) != 0UL) break;
            }
            // SW
            for (int rr = r - 1, ff = f - 1; rr >= 0 && ff >= 0; rr--, ff--)
            {
                int s = rr * 8 + ff;
                attacks |= 1UL << s;
                if (((occ >> s) & 1UL) != 0UL) break;
            }
            return attacks;
        }

        public static ulong GetKnightAttacks(int sq) => KnightAttacksTable[sq];
        public static ulong GetKingAttacks(int sq) => KingAttacksTable[sq];
        public static ulong GetQueenAttacks(int sq, ulong occ) => RookAttacks(sq, occ) | BishopAttacks(sq, occ);

        private static ulong GenerateKnightAttacksInternal(int sq)
        {
            ulong b = 1UL << sq;
            ulong attacks = 0;
            attacks |= (b << 17) & 0xfefefefefefefe00UL;
            attacks |= (b << 15) & 0x7f7f7f7f7f7f7f00UL;
            attacks |= (b << 10) & 0xfcfcfcfcfcfcfc00UL;
            attacks |= (b << 6) & 0x3f3f3f3f3f3f3f00UL;
            attacks |= (b >> 6) & 0x00fcfcfcfcfcfcfcUL;
            attacks |= (b >> 10) & 0x003f3f3f3f3f3f3fUL;
            attacks |= (b >> 15) & 0x00fefefefefefefeUL;
            attacks |= (b >> 17) & 0x007f7f7f7f7f7f7fUL;
            return attacks;
        }

        private static ulong GenerateKingAttacksInternal(int sq)
        {
            ulong b = 1UL << sq;
            ulong attacks = 0;
            attacks |= (b << 8);
            attacks |= (b >> 8);
            attacks |= (b << 1) & 0xfefefefefefefefeUL;
            attacks |= (b >> 1) & 0x7f7f7f7f7f7f7f7fUL;
            attacks |= (b << 9) & 0xfefefefefefefefeUL;
            attacks |= (b >> 9) & 0x7f7f7f7f7f7f7f7fUL;
            attacks |= (b << 7) & 0x7f7f7f7f7f7f7f7fUL;
            attacks |= (b >> 7) & 0xfefefefefefefefeUL;
            return attacks;
        }
    }
}
