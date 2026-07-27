using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// A fixed-size bitset over entity slots, backed by a <c>ulong[]</c> allocated once. This is the
/// currency of AOI bookkeeping: per-subscriber known-sets and visibility sets are all
/// <see cref="SlotBitset"/>s, and per-tick diffing (enters, exits, dirty-known intersection) is done
/// with allocation-free word-wise enumerators built on <see cref="BitOperations.TrailingZeroCount(ulong)"/>.
/// 600 clients × 4096 slots is ~300 KB of long-lived words and zero per-tick garbage.
/// </summary>
/// <remarks>
/// Not thread-safe — owned by one room's tick thread, like everything in this module. Enumerators
/// snapshot each 64-bit word as they load it, so clearing a bit that the enumerator has
/// <i>already returned</i> (or any bit in an earlier word) while iterating is safe; setting new bits
/// mid-iteration is not guaranteed to be observed.
/// </remarks>
public sealed class SlotBitset
{
    private readonly ulong[] _words;
    private readonly int _capacity;

    /// <summary>Creates a bitset able to hold bits <c>0..capacity-1</c>, all initially clear.</summary>
    public SlotBitset(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _words = new ulong[(capacity + 63) >> 6];
    }

    /// <summary>Number of addressable bits.</summary>
    public int Capacity => _capacity;

    /// <summary>Backing word count (shared by all bitsets of equal capacity).</summary>
    public int WordCount => _words.Length;

    /// <summary>Clears every bit.</summary>
    public void Clear() => Array.Clear(_words, 0, _words.Length);

    /// <summary>Sets bit <paramref name="index"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index)
    {
        Debug.Assert((uint)index < (uint)_capacity, "bit index out of range");
        _words[index >> 6] |= 1ul << (index & 63);
    }

    /// <summary>Clears bit <paramref name="index"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unset(int index)
    {
        Debug.Assert((uint)index < (uint)_capacity, "bit index out of range");
        _words[index >> 6] &= ~(1ul << (index & 63));
    }

    /// <summary>Reads bit <paramref name="index"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int index)
    {
        Debug.Assert((uint)index < (uint)_capacity, "bit index out of range");
        return (_words[index >> 6] & (1ul << (index & 63))) != 0;
    }

    /// <summary>Overwrites this bitset with <paramref name="source"/>. Capacities must match.</summary>
    public void CopyFrom(SlotBitset source)
    {
        Debug.Assert(source._words.Length == _words.Length, "bitset capacity mismatch");
        Array.Copy(source._words, _words, _words.Length);
    }

    /// <summary>Adds every bit of <paramref name="other"/> (<c>this |= other</c>). Capacities must match.</summary>
    public void UnionWith(SlotBitset other)
    {
        Debug.Assert(other._words.Length == _words.Length, "bitset capacity mismatch");
        ulong[] words = _words;
        ulong[] source = other._words;
        for (int i = 0; i < words.Length; i++)
        {
            words[i] |= source[i];
        }
    }

    /// <summary>Keeps only bits also set in <paramref name="other"/> (<c>this &amp;= other</c>).</summary>
    public void IntersectWith(SlotBitset other)
    {
        Debug.Assert(other._words.Length == _words.Length, "bitset capacity mismatch");
        ulong[] words = _words;
        ulong[] source = other._words;
        for (int i = 0; i < words.Length; i++)
        {
            words[i] &= source[i];
        }
    }

    /// <summary>
    /// Adds the bits set in all three operands (<c>this |= a &amp; b &amp; c</c>) in a single word-wise
    /// pass. This is how a client's owed-update set is folded in without materialising an intermediate
    /// bitset — and without an allocation on the commit path.
    /// </summary>
    public void UnionWithIntersection(SlotBitset a, SlotBitset b, SlotBitset c)
    {
        Debug.Assert(a._words.Length == _words.Length, "bitset capacity mismatch");
        Debug.Assert(b._words.Length == _words.Length, "bitset capacity mismatch");
        Debug.Assert(c._words.Length == _words.Length, "bitset capacity mismatch");
        ulong[] words = _words;
        ulong[] wa = a._words;
        ulong[] wb = b._words;
        ulong[] wc = c._words;
        for (int i = 0; i < words.Length; i++)
        {
            words[i] |= wa[i] & wb[i] & wc[i];
        }
    }

    /// <summary>Population count. Metrics-grade utility, not on the per-entity hot path.</summary>
    public int Count()
    {
        int count = 0;
        ulong[] words = _words;
        for (int i = 0; i < words.Length; i++)
        {
            count += BitOperations.PopCount(words[i]);
        }

        return count;
    }

    /// <summary>Allocation-free enumeration of every set bit, ascending.</summary>
    public SingleEnumerator EnumerateSetBits() => new(_words);

    /// <summary>Bits set in both this and <paramref name="other"/> (<c>this ∧ other</c>).</summary>
    public PairEnumerator EnumerateAnd(SlotBitset other) => new(_words, other._words, PairOp.And);

    /// <summary>Bits set in this but not in <paramref name="other"/> (<c>this ∧ ¬other</c>).</summary>
    public PairEnumerator EnumerateAndNot(SlotBitset other) => new(_words, other._words, PairOp.AndNot);

    /// <summary>Bits set in exactly one of this and <paramref name="other"/> (<c>this ⊕ other</c>).</summary>
    public PairEnumerator EnumerateXor(SlotBitset other) => new(_words, other._words, PairOp.Xor);

    /// <summary>Word-combining operation for <see cref="PairEnumerator"/>.</summary>
    public enum PairOp : byte
    {
        /// <summary><c>a &amp; b</c>.</summary>
        And = 0,

        /// <summary><c>a &amp; ~b</c>.</summary>
        AndNot = 1,

        /// <summary><c>a ^ b</c>.</summary>
        Xor = 2,
    }

    /// <summary>
    /// Struct enumerator over the set bits of one bitset. Duck-typed for <c>foreach</c> — no interface,
    /// no boxing, no allocation.
    /// </summary>
    public struct SingleEnumerator
    {
        private readonly ulong[] _words;
        private int _wordIndex;
        private ulong _current;   // snapshot of the remaining bits of the loaded word
        private int _currentBit;

        internal SingleEnumerator(ulong[] words)
        {
            _words = words;
            _wordIndex = -1;
            _current = 0;
            _currentBit = -1;
        }

        /// <summary>Bit index of the current element.</summary>
        public readonly int Current => _currentBit;

        /// <summary>Advances to the next set bit.</summary>
        public bool MoveNext()
        {
            while (_current == 0)
            {
                _wordIndex++;
                if (_wordIndex >= _words.Length)
                {
                    return false;
                }

                _current = _words[_wordIndex];
            }

            int tz = BitOperations.TrailingZeroCount(_current);
            _current &= _current - 1;   // clear lowest set bit
            _currentBit = (_wordIndex << 6) + tz;
            return true;
        }

        /// <summary>Enables <c>foreach</c> directly over the enumerator.</summary>
        public readonly SingleEnumerator GetEnumerator() => this;
    }

    /// <summary>
    /// Struct enumerator over the bit positions of a word-wise combination of two equal-capacity
    /// bitsets. Computes each combined word on load, so no intermediate bitset is materialised.
    /// </summary>
    public struct PairEnumerator
    {
        private readonly ulong[] _a;
        private readonly ulong[] _b;
        private readonly PairOp _op;
        private int _wordIndex;
        private ulong _current;
        private int _currentBit;

        internal PairEnumerator(ulong[] a, ulong[] b, PairOp op)
        {
            Debug.Assert(a.Length == b.Length, "bitset capacity mismatch");
            _a = a;
            _b = b;
            _op = op;
            _wordIndex = -1;
            _current = 0;
            _currentBit = -1;
        }

        /// <summary>Bit index of the current element.</summary>
        public readonly int Current => _currentBit;

        /// <summary>Advances to the next bit of the combined set.</summary>
        public bool MoveNext()
        {
            while (_current == 0)
            {
                _wordIndex++;
                if (_wordIndex >= _a.Length)
                {
                    return false;
                }

                ulong a = _a[_wordIndex];
                ulong b = _b[_wordIndex];
                _current = _op switch
                {
                    PairOp.And => a & b,
                    PairOp.AndNot => a & ~b,
                    _ => a ^ b,
                };
            }

            int tz = BitOperations.TrailingZeroCount(_current);
            _current &= _current - 1;
            _currentBit = (_wordIndex << 6) + tz;
            return true;
        }

        /// <summary>Enables <c>foreach</c> directly over the enumerator.</summary>
        public readonly PairEnumerator GetEnumerator() => this;
    }
}
