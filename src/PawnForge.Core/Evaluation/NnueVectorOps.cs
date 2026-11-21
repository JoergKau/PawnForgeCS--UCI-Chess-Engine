namespace PawnForge.Core.Evaluation;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

/// <summary>
/// Vector-width kernels for the scheme-2 NNUE accumulator.
///
/// <para>Two implementations of every kernel exist: one over
/// <see cref="Vector{T}"/> (128-bit on ARM64, 256-bit on an AVX2 x64 host) and
/// one over <see cref="Vector512{T}"/> for hosts where AVX-512 is available and
/// profitable. The dispatch is a JIT-time constant, so exactly one arm is
/// reachable per target and the check adds no per-make work.</para>
///
/// <para>BIT-EXACTNESS: all kernels compute the same wrapping i16 add/sub
/// chain, only the lane count differs. Integer addition is exact and
/// order-free, so the result is identical to the scalar reference. The tail
/// lanes that do not fill a whole vector keep the parent's value (updates) or
/// the bias (refresh), exactly as the row-at-a-time form did.</para>
/// </summary>
internal static class NnueVectorOps
{
    /// <summary>Fused copy + one add + one subtract (quiet move).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fused1Add1Sub(ref short parent, ref short child, ref short weights,
        int addRow, int subRow, int l1)
    {
        if (Vector512.IsHardwareAccelerated)
            Fused1Add1Sub512(ref parent, ref child, ref weights, addRow, subRow, l1);
        else
            Fused1Add1SubV(ref parent, ref child, ref weights, addRow, subRow, l1);
    }

    /// <summary>Fused copy + one add + two subtracts (capture / promotion).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fused1Add2Sub(ref short parent, ref short child, ref short weights,
        int addRow, int subRow0, int subRow1, int l1)
    {
        if (Vector512.IsHardwareAccelerated)
            Fused1Add2Sub512(ref parent, ref child, ref weights, addRow, subRow0, subRow1, l1);
        else
            Fused1Add2SubV(ref parent, ref child, ref weights, addRow, subRow0, subRow1, l1);
    }

    /// <summary>Fused copy + two adds + two subtracts (castling).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fused2Add2Sub(ref short parent, ref short child, ref short weights,
        int addRow0, int addRow1, int subRow0, int subRow1, int l1)
    {
        if (Vector512.IsHardwareAccelerated)
            Fused2Add2Sub512(ref parent, ref child, ref weights,
                addRow0, addRow1, subRow0, subRow1, l1);
        else
            Fused2Add2SubV(ref parent, ref child, ref weights,
                addRow0, addRow1, subRow0, subRow1, l1);
    }

    /// <summary>Fused copy + a runtime row list (null move / unknown shape).</summary>
    public static void FusedGeneric(ref short parent, ref short child, ref short weights,
        ReadOnlySpan<int> adds, ReadOnlySpan<int> subs, int l1)
    {
        if (Vector512.IsHardwareAccelerated)
            FusedGeneric512(ref parent, ref child, ref weights, adds, subs, l1);
        else
            FusedGenericV(ref parent, ref child, ref weights, adds, subs, l1);
    }

    /// <summary>Fused full refresh: bias + every active feature row, one store
    /// per lane chunk.</summary>
    public static void Refresh(ref short acc, ref short bias, ref short weights,
        ReadOnlySpan<int> features, int count, int l1)
    {
        if (Vector512.IsHardwareAccelerated)
            Refresh512(ref acc, ref bias, ref weights, features, count, l1);
        else
            RefreshV(ref acc, ref bias, ref weights, features, count, l1);
    }

    // ---- portable (Vector<T>) ---------------------------------------------------

    internal static void Fused1Add1SubV(ref short p, ref short c, ref short w,
        int addRow, int subRow, int l1)
    {
        int width = Vector<short>.Count;
        int main = l1 - (l1 % width);
        for (int offset = 0; offset < main; offset += width)
        {
            nuint o = (nuint)offset;
            Vector<short> v = Vector.LoadUnsafe(ref p, o);
            v += Vector.LoadUnsafe(ref w, (nuint)addRow + o);
            v -= Vector.LoadUnsafe(ref w, (nuint)subRow + o);
            Vector.StoreUnsafe(v, ref c, o);
        }
        ApplyTail(ref p, ref c, ref w, addRow, -1, subRow, -1, main, l1);
    }

    internal static void Fused1Add2SubV(ref short p, ref short c, ref short w,
        int addRow, int subRow0, int subRow1, int l1)
    {
        int width = Vector<short>.Count;
        int main = l1 - (l1 % width);
        for (int offset = 0; offset < main; offset += width)
        {
            nuint o = (nuint)offset;
            Vector<short> v = Vector.LoadUnsafe(ref p, o);
            v += Vector.LoadUnsafe(ref w, (nuint)addRow + o);
            v -= Vector.LoadUnsafe(ref w, (nuint)subRow0 + o);
            v -= Vector.LoadUnsafe(ref w, (nuint)subRow1 + o);
            Vector.StoreUnsafe(v, ref c, o);
        }
        ApplyTail(ref p, ref c, ref w, addRow, -1, subRow0, subRow1, main, l1);
    }

    internal static void Fused2Add2SubV(ref short p, ref short c, ref short w,
        int addRow0, int addRow1, int subRow0, int subRow1, int l1)
    {
        int width = Vector<short>.Count;
        int main = l1 - (l1 % width);
        for (int offset = 0; offset < main; offset += width)
        {
            nuint o = (nuint)offset;
            Vector<short> v = Vector.LoadUnsafe(ref p, o);
            v += Vector.LoadUnsafe(ref w, (nuint)addRow0 + o);
            v += Vector.LoadUnsafe(ref w, (nuint)addRow1 + o);
            v -= Vector.LoadUnsafe(ref w, (nuint)subRow0 + o);
            v -= Vector.LoadUnsafe(ref w, (nuint)subRow1 + o);
            Vector.StoreUnsafe(v, ref c, o);
        }
        ApplyTail(ref p, ref c, ref w, addRow0, addRow1, subRow0, subRow1, main, l1);
    }

    internal static void FusedGenericV(ref short p, ref short c, ref short w,
        ReadOnlySpan<int> adds, ReadOnlySpan<int> subs, int l1)
    {
        int width = Vector<short>.Count;
        int main = l1 - (l1 % width);
        for (int offset = 0; offset < main; offset += width)
        {
            nuint o = (nuint)offset;
            Vector<short> v = Vector.LoadUnsafe(ref p, o);
            for (int k = 0; k < adds.Length; k++)
                v += Vector.LoadUnsafe(ref w, (nuint)(adds[k] * l1) + o);
            for (int k = 0; k < subs.Length; k++)
                v -= Vector.LoadUnsafe(ref w, (nuint)(subs[k] * l1) + o);
            Vector.StoreUnsafe(v, ref c, o);
        }
        ApplyTailGeneric(ref p, ref c, ref w, adds, subs, main, l1);
    }

    internal static void RefreshV(ref short a, ref short b, ref short w,
        ReadOnlySpan<int> features, int count, int l1)
    {
        int width = Vector<short>.Count;
        int main = l1 - (l1 % width);
        for (int offset = 0; offset < main; offset += width)
        {
            nuint o = (nuint)offset;
            // Four independent partial sums break the dependent add chain of the
            // active rows. The rows are independent, so the result is exact and
            // order-free; the final tree-add restores the scalar value.
            Vector<short> s0 = Vector.LoadUnsafe(ref b, o);
            Vector<short> s1 = Vector<short>.Zero;
            Vector<short> s2 = Vector<short>.Zero;
            Vector<short> s3 = Vector<short>.Zero;
            int k = 0;
            for (; k + 4 <= count; k += 4)
            {
                s0 += Vector.LoadUnsafe(ref w, (nuint)(features[k] * l1) + o);
                s1 += Vector.LoadUnsafe(ref w, (nuint)(features[k + 1] * l1) + o);
                s2 += Vector.LoadUnsafe(ref w, (nuint)(features[k + 2] * l1) + o);
                s3 += Vector.LoadUnsafe(ref w, (nuint)(features[k + 3] * l1) + o);
            }
            for (; k < count; k++)
                s0 += Vector.LoadUnsafe(ref w, (nuint)(features[k] * l1) + o);
            Vector<short> sum = (s0 + s1) + (s2 + s3);
            Vector.StoreUnsafe(sum, ref a, o);
        }
        RefreshTail(ref a, ref b, ref w, features, count, main, l1);
    }

    // ---- AVX-512 (Vector512<T>) -------------------------------------------------

    internal static void Fused1Add1Sub512(ref short p, ref short c, ref short w,
        int addRow, int subRow, int l1)
    {
        int width = Vector512<short>.Count;
        int main = l1 - (l1 % width);
        for (int offset = 0; offset < main; offset += width)
        {
            nuint o = (nuint)offset;
            Vector512<short> v = Vector512.LoadUnsafe(ref p, o);
            v += Vector512.LoadUnsafe(ref w, (nuint)addRow + o);
            v -= Vector512.LoadUnsafe(ref w, (nuint)subRow + o);
            Vector512.StoreUnsafe(v, ref c, o);
        }
        ApplyTail(ref p, ref c, ref w, addRow, -1, subRow, -1, main, l1);
    }

    internal static void Fused1Add2Sub512(ref short p, ref short c, ref short w,
        int addRow, int subRow0, int subRow1, int l1)
    {
        int width = Vector512<short>.Count;
        int main = l1 - (l1 % width);
        for (int offset = 0; offset < main; offset += width)
        {
            nuint o = (nuint)offset;
            Vector512<short> v = Vector512.LoadUnsafe(ref p, o);
            v += Vector512.LoadUnsafe(ref w, (nuint)addRow + o);
            v -= Vector512.LoadUnsafe(ref w, (nuint)subRow0 + o);
            v -= Vector512.LoadUnsafe(ref w, (nuint)subRow1 + o);
            Vector512.StoreUnsafe(v, ref c, o);
        }
        ApplyTail(ref p, ref c, ref w, addRow, -1, subRow0, subRow1, main, l1);
    }

    internal static void Fused2Add2Sub512(ref short p, ref short c, ref short w,
        int addRow0, int addRow1, int subRow0, int subRow1, int l1)
    {
        int width = Vector512<short>.Count;
        int main = l1 - (l1 % width);
        for (int offset = 0; offset < main; offset += width)
        {
            nuint o = (nuint)offset;
            Vector512<short> v = Vector512.LoadUnsafe(ref p, o);
            v += Vector512.LoadUnsafe(ref w, (nuint)addRow0 + o);
            v += Vector512.LoadUnsafe(ref w, (nuint)addRow1 + o);
            v -= Vector512.LoadUnsafe(ref w, (nuint)subRow0 + o);
            v -= Vector512.LoadUnsafe(ref w, (nuint)subRow1 + o);
            Vector512.StoreUnsafe(v, ref c, o);
        }
        ApplyTail(ref p, ref c, ref w, addRow0, addRow1, subRow0, subRow1, main, l1);
    }

    internal static void FusedGeneric512(ref short p, ref short c, ref short w,
        ReadOnlySpan<int> adds, ReadOnlySpan<int> subs, int l1)
    {
        int width = Vector512<short>.Count;
        int main = l1 - (l1 % width);
        for (int offset = 0; offset < main; offset += width)
        {
            nuint o = (nuint)offset;
            Vector512<short> v = Vector512.LoadUnsafe(ref p, o);
            for (int k = 0; k < adds.Length; k++)
                v += Vector512.LoadUnsafe(ref w, (nuint)(adds[k] * l1) + o);
            for (int k = 0; k < subs.Length; k++)
                v -= Vector512.LoadUnsafe(ref w, (nuint)(subs[k] * l1) + o);
            Vector512.StoreUnsafe(v, ref c, o);
        }
        ApplyTailGeneric(ref p, ref c, ref w, adds, subs, main, l1);
    }

    internal static void Refresh512(ref short a, ref short b, ref short w,
        ReadOnlySpan<int> features, int count, int l1)
    {
        int width = Vector512<short>.Count;
        int main = l1 - (l1 % width);
        for (int offset = 0; offset < main; offset += width)
        {
            nuint o = (nuint)offset;
            Vector512<short> s0 = Vector512.LoadUnsafe(ref b, o);
            Vector512<short> s1 = Vector512<short>.Zero;
            Vector512<short> s2 = Vector512<short>.Zero;
            Vector512<short> s3 = Vector512<short>.Zero;
            int k = 0;
            for (; k + 4 <= count; k += 4)
            {
                s0 += Vector512.LoadUnsafe(ref w, (nuint)(features[k] * l1) + o);
                s1 += Vector512.LoadUnsafe(ref w, (nuint)(features[k + 1] * l1) + o);
                s2 += Vector512.LoadUnsafe(ref w, (nuint)(features[k + 2] * l1) + o);
                s3 += Vector512.LoadUnsafe(ref w, (nuint)(features[k + 3] * l1) + o);
            }
            for (; k < count; k++)
                s0 += Vector512.LoadUnsafe(ref w, (nuint)(features[k] * l1) + o);
            Vector512<short> sum = (s0 + s1) + (s2 + s3);
            Vector512.StoreUnsafe(sum, ref a, o);
        }
        RefreshTail(ref a, ref b, ref w, features, count, main, l1);
    }

    /// <summary>Computes the lanes that do not fill a whole vector. Doing the
    /// tail scalar-wise instead of copying the bias/parent makes the result
    /// INDEPENDENT of the host's vector width: a 32-lane host and an 8-lane host
    /// produce the same accumulator at every legal L1 (the 144/160 widths are the
    /// ones where they would otherwise disagree).</summary>
    private static void RefreshTail(ref short a, ref short b, ref short w,
        ReadOnlySpan<int> features, int count, int main, int l1)
    {
        for (int i = main; i < l1; i++)
        {
            int sum = Unsafe.Add(ref b, i);
            for (int k = 0; k < count; k++)
                sum += Unsafe.Add(ref w, features[k] * l1 + i);
            Unsafe.Add(ref a, i) = unchecked((short)sum);
        }
    }

    /// <summary>Computes the lanes that do not fill a whole vector for an
    /// update. Same width-independence argument as <see cref="RefreshTail"/>.
    /// A negative row offset means "absent".</summary>
    private static void ApplyTail(ref short p, ref short c, ref short w,
        int add0, int add1, int sub0, int sub1, int main, int l1)
    {
        for (int i = main; i < l1; i++)
        {
            int value = Unsafe.Add(ref p, i);
            if (add0 >= 0) value += Unsafe.Add(ref w, add0 + i);
            if (add1 >= 0) value += Unsafe.Add(ref w, add1 + i);
            if (sub0 >= 0) value -= Unsafe.Add(ref w, sub0 + i);
            if (sub1 >= 0) value -= Unsafe.Add(ref w, sub1 + i);
            Unsafe.Add(ref c, i) = unchecked((short)value);
        }
    }

    /// <summary>Generic tail for the runtime row list.</summary>
    private static void ApplyTailGeneric(ref short p, ref short c, ref short w,
        ReadOnlySpan<int> adds, ReadOnlySpan<int> subs, int main, int l1)
    {
        for (int i = main; i < l1; i++)
        {
            int value = Unsafe.Add(ref p, i);
            for (int k = 0; k < adds.Length; k++)
                value += Unsafe.Add(ref w, adds[k] * l1 + i);
            for (int k = 0; k < subs.Length; k++)
                value -= Unsafe.Add(ref w, subs[k] * l1 + i);
            Unsafe.Add(ref c, i) = unchecked((short)value);
        }
    }
}
