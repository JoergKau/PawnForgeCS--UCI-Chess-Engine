using System.Threading;
using System.Runtime.InteropServices;

namespace PawnForge.Core
{
    /// <summary>
    /// Minimal, well-typed atomic helpers for the engine's low-level
    /// concurrency needs. Designed to be small and explicit so tests/review
    /// can verify semantics. Currently only the bool helpers (ReadBool/
    /// WriteBool/ExchangeBool) are used - by SearchSignal's stop flag; the
    /// long/ulong/int variants are kept for future lock-free structures.
    /// </summary>
    public static class Atomics
    {
        // 64-bit read (uses Interlocked.Read to be safe on all platforms)
        public static long Read(ref long location) => Interlocked.Read(ref location);

        // 64-bit write (volatile semantics)
        public static void Write(ref long location, long value) => Volatile.Write(ref location, value);

        // 64-bit exchange
        public static long Exchange(ref long location, long value) => Interlocked.Exchange(ref location, value);

        // 64-bit compare-exchange
        public static long CompareExchange(ref long location, long value, long comparand) => Interlocked.CompareExchange(ref location, value, comparand);

        // 64-bit add
        public static long Add(ref long location, long value) => Interlocked.Add(ref location, value);


        // Atomic AND/OR (Logic)
        public static long And(ref long location, long value) => Interlocked.And(ref location, value);
        public static long Or(ref long location, long value) => Interlocked.Or(ref location, value);

        // Atomic unsigned (ulong) helpers implemented via MemoryMarshal cast to long to avoid Unsafe API requirement.
        public static ulong Read(ref ulong location)
        {
            ref long asLong = ref MemoryMarshal.Cast<ulong, long>(MemoryMarshal.CreateSpan(ref location, 1))[0];
            return unchecked((ulong)Interlocked.Read(ref asLong));
        }

        public static void Write(ref ulong location, ulong value)
        {
            ref long asLong = ref MemoryMarshal.Cast<ulong, long>(MemoryMarshal.CreateSpan(ref location, 1))[0];
            Volatile.Write(ref asLong, unchecked((long)value));
        }

        public static ulong Exchange(ref ulong location, ulong value)
        {
            ref long asLong = ref MemoryMarshal.Cast<ulong, long>(MemoryMarshal.CreateSpan(ref location, 1))[0];
            long old = Interlocked.Exchange(ref asLong, unchecked((long)value));
            return unchecked((ulong)old);
        }

        public static ulong CompareExchange(ref ulong location, ulong value, ulong comparand)
        {
            ref long asLong = ref MemoryMarshal.Cast<ulong, long>(MemoryMarshal.CreateSpan(ref location, 1))[0];
            long res = Interlocked.CompareExchange(ref asLong, unchecked((long)value), unchecked((long)comparand));
            return unchecked((ulong)res);
        }

        public static ulong Add(ref ulong location, long value)
        {
            ref long asLong = ref MemoryMarshal.Cast<ulong, long>(MemoryMarshal.CreateSpan(ref location, 1))[0];
            long res = Interlocked.Add(ref asLong, value);
            return unchecked((ulong)res);
        }

        public static ulong And(ref ulong location, ulong value)
        {
            ref long asLong = ref MemoryMarshal.Cast<ulong, long>(MemoryMarshal.CreateSpan(ref location, 1))[0];
            long res = Interlocked.And(ref asLong, unchecked((long)value));
            return unchecked((ulong)res);
        }

        public static ulong Or(ref ulong location, ulong value)
        {
            ref long asLong = ref MemoryMarshal.Cast<ulong, long>(MemoryMarshal.CreateSpan(ref location, 1))[0];
            long res = Interlocked.Or(ref asLong, unchecked((long)value));
            return unchecked((ulong)res);
        }

        // Atomic increment/decrement for int (common counters)
        public static int Increment(ref int location) => Interlocked.Increment(ref location);
        public static int Decrement(ref int location) => Interlocked.Decrement(ref location);
        
        // Boolean helpers (int-backed)
        public static bool ReadBool(ref int location) => Volatile.Read(ref location) != 0;
        public static void WriteBool(ref int location, bool value) => Volatile.Write(ref location, value ? 1 : 0);
        public static bool ExchangeBool(ref int location, bool value) => Interlocked.Exchange(ref location, value ? 1 : 0) != 0;

        // Volatile read/write helpers for references
        public static T ReadRef<T>(ref T location) where T : class => Volatile.Read(ref location);
        public static void WriteRef<T>(ref T location, T value) where T : class => Volatile.Write(ref location, value);
    }
}