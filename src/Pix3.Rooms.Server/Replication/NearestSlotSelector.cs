using System.Diagnostics;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// Bounded "keep the k nearest" selection over entity slots, backed by a fixed-size max-heap keyed on
/// <b>squared</b> distance. One instance is scratch shared by every client in a room (room logic is
/// single-threaded), so the dogpile path allocates nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a heap and not a sort.</b> An AOI radius does not bound worst-case egress: 600 players stacked
/// on one point are all inside each other's radius, which is exactly the case
/// <c>MaxVisibleEntities</c> exists to stop. Selecting the k nearest of n candidates costs
/// <c>O(n log k)</c> here — the root is the current k-th nearest, so a candidate is rejected by one
/// comparison — whereas sorting the candidate list would cost <c>O(n log n)</c> per client per tick.
/// </para>
/// <para>
/// <b>Squared distances only.</b> Ranking by <c>dx² + dy²</c> is order-equivalent to ranking by distance,
/// so no square root is ever computed. Positions come from the entity table's dequantized float columns
/// and are therefore always finite, so no NaN can poison the ordering.
/// </para>
/// <para>
/// The heap keeps the <i>largest</i> distance at the root: that is what makes "is this candidate better
/// than the worst one I am keeping?" a single peek.
/// </para>
/// </remarks>
public sealed class NearestSlotSelector
{
    private readonly float[] _distanceSquared;
    private readonly int[] _slot;
    private int _count;

    /// <summary>Allocates a selector that keeps at most <paramref name="capacity"/> slots.</summary>
    public NearestSlotSelector(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _distanceSquared = new float[capacity];
        _slot = new int[capacity];
    }

    /// <summary>Maximum number of slots this selector keeps — the k in k-nearest.</summary>
    public int Capacity => _slot.Length;

    /// <summary>Slots currently kept.</summary>
    public int Count => _count;

    /// <summary>Squared distance of the worst kept slot. Only meaningful while <see cref="Count"/> &gt; 0.</summary>
    public float WorstDistanceSquared => _distanceSquared[0];

    /// <summary>Empties the selector for a new client's pass. O(1) — no array clearing needed.</summary>
    public void Reset() => _count = 0;

    /// <summary>
    /// Offers one candidate. Kept while there is room, or when it beats the current worst; otherwise
    /// discarded by a single comparison.
    /// </summary>
    /// <returns>True when the candidate is now part of the kept set.</returns>
    public bool Offer(int slot, float distanceSquared)
    {
        if (_count < _slot.Length)
        {
            int i = _count++;
            _distanceSquared[i] = distanceSquared;
            _slot[i] = slot;
            SiftUp(i);
            return true;
        }

        // The root is the worst kept candidate, so this is the whole rejection test.
        if (distanceSquared >= _distanceSquared[0])
        {
            return false;
        }

        _distanceSquared[0] = distanceSquared;
        _slot[0] = slot;
        SiftDown(0);
        return true;
    }

    /// <summary>
    /// Overwrites <paramref name="destination"/> with exactly the kept slots. The destination is cleared
    /// first, so the result is the selection and nothing else.
    /// </summary>
    public void FillBitset(SlotBitset destination)
    {
        destination.Clear();
        for (int i = 0; i < _count; i++)
        {
            destination.Set(_slot[i]);
        }
    }

    private void SiftUp(int index)
    {
        float[] dist = _distanceSquared;
        int[] slots = _slot;
        while (index > 0)
        {
            int parent = (index - 1) >> 1;
            if (dist[parent] >= dist[index])
            {
                return;
            }

            (dist[parent], dist[index]) = (dist[index], dist[parent]);
            (slots[parent], slots[index]) = (slots[index], slots[parent]);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        float[] dist = _distanceSquared;
        int[] slots = _slot;
        int count = _count;
        while (true)
        {
            int left = (index << 1) + 1;
            if (left >= count)
            {
                return;
            }

            int largest = left;
            int right = left + 1;
            if (right < count && dist[right] > dist[left])
            {
                largest = right;
            }

            if (dist[index] >= dist[largest])
            {
                return;
            }

            (dist[largest], dist[index]) = (dist[index], dist[largest]);
            (slots[largest], slots[index]) = (slots[index], slots[largest]);
            index = largest;
        }
    }

    /// <summary>Debug-only heap invariant check: every parent is at least as far as its children.</summary>
    [Conditional("DEBUG")]
    internal void AssertHeapInvariant()
    {
        for (int i = 1; i < _count; i++)
        {
            int parent = (i - 1) >> 1;
            Debug.Assert(_distanceSquared[parent] >= _distanceSquared[i], "max-heap invariant broken");
        }
    }
}
