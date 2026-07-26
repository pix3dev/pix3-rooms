using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// Uniform spatial hash over unbounded 2D space, rebuilt from the entity table's packed live-slot
/// list every tick with a counting sort — no dictionaries, no per-cell lists, no per-tick allocation.
/// </summary>
/// <remarks>
/// <para><b>Data layout.</b> Cell coordinates <c>(floor(x/cell), floor(y/cell))</c> are hashed into a
/// power-of-two bucket table, so entities anywhere in float space (including garbage client
/// coordinates) land in a bucket instead of crashing. <see cref="Build"/> runs a counting sort:
/// pass 1 counts entries per bucket, a prefix sum turns counts into offsets, pass 2 scatters
/// <c>(slot, x, y)</c> into three parallel entry arrays grouped by bucket. Positions are copied into
/// the entry arrays so queries stream contiguously instead of re-reading the table.</para>
/// <para><b>Query semantics.</b> Queries visit the cell rectangle covering the circle and apply an
/// <i>exact distance check</i> per candidate (cell granularity alone would make AOI edges depend on
/// cell alignment). Hash aliasing can only ever re-test or duplicate candidates — the bitset
/// destination makes duplicates idempotent, and an aliased far-away entity fails the distance check —
/// it can never cause a miss, because an in-radius entity's true cell is always inside the visited
/// rectangle. Entities with non-finite positions fail every distance comparison and are simply
/// invisible.</para>
/// <para><b>Hysteresis.</b> <see cref="QueryRadiusWithHysteresis"/> fills two sets in one pass: an
/// inner (enter) set and an outer (exit) set. Callers admit entities at the inner radius but only
/// expel them beyond the outer one, so an entity oscillating at the boundary does not flap in and out
/// of AOI every tick.</para>
/// </remarks>
public sealed class SpatialHashGrid
{
    private readonly float _invCellSize;
    private readonly int _bucketCount;   // power of two
    private readonly int _bucketMask;

    private readonly int[] _counts;      // per-bucket entry count (build scratch)
    private readonly int[] _starts;      // per-bucket first entry index, +1 sentinel slot
    private readonly int[] _cursors;     // per-bucket scatter cursor (build scratch)
    private readonly int[] _entryBucket; // per-live-entity bucket (build scratch)

    private readonly int[] _entrySlot;   // entries grouped by bucket after Build
    private readonly float[] _entryX;
    private readonly float[] _entryY;

    private int _entryCount;

    /// <summary>
    /// Creates a grid for at most <paramref name="capacity"/> entities with the given cell size
    /// (normally ≈ the AOI radius, keeping queries inside a 3×3 neighbourhood).
    /// </summary>
    public SpatialHashGrid(int capacity, float cellSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "must be finite and > 0");
        }

        _invCellSize = 1f / cellSize;
        _bucketCount = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacity, 64));
        _bucketMask = _bucketCount - 1;

        _counts = new int[_bucketCount];
        _starts = new int[_bucketCount + 1];
        _cursors = new int[_bucketCount];
        _entryBucket = new int[capacity];
        _entrySlot = new int[capacity];
        _entryX = new float[capacity];
        _entryY = new float[capacity];
    }

    /// <summary>Entries indexed by the last <see cref="Build"/>.</summary>
    public int EntryCount => _entryCount;

    /// <summary>
    /// Rebuilds the grid from the table's packed live slots. O(live + buckets), zero allocation.
    /// Call once per tick before any query.
    /// </summary>
    public void Build(EntityTable table)
    {
        ReadOnlySpan<int> live = table.LiveSlots;
        _entryCount = live.Length;
        Debug.Assert(_entryCount <= _entrySlot.Length, "grid capacity below table capacity");

        Array.Clear(_counts, 0, _bucketCount);

        float[] tableX = table.X;
        float[] tableY = table.Y;

        // Pass 1: bucket + count.
        for (int i = 0; i < live.Length; i++)
        {
            int slot = live[i];
            int bucket = BucketOf(tableX[slot], tableY[slot]);
            _entryBucket[i] = bucket;
            _counts[bucket]++;
        }

        // Prefix sum: counts -> start offsets (with end sentinel).
        int sum = 0;
        for (int b = 0; b < _bucketCount; b++)
        {
            _starts[b] = sum;
            _cursors[b] = sum;
            sum += _counts[b];
        }

        _starts[_bucketCount] = sum;

        // Pass 2: scatter (slot, x, y) grouped by bucket.
        for (int i = 0; i < live.Length; i++)
        {
            int slot = live[i];
            int e = _cursors[_entryBucket[i]]++;
            _entrySlot[e] = slot;
            _entryX[e] = tableX[slot];
            _entryY[e] = tableY[slot];
        }
    }

    /// <summary>
    /// Sets the bit of every live slot whose position lies within <paramref name="radius"/> of
    /// <c>(x, y)</c> (exact circle test). <paramref name="dst"/> is cleared first.
    /// </summary>
    public void QueryRadius(float x, float y, float radius, ref SlotBitset dst)
    {
        dst.Clear();
        QueryCore(x, y, radius, radius, dst, dst);
    }

    /// <summary>
    /// One-pass double query for AOI hysteresis: fills <paramref name="inner"/> with slots within
    /// <paramref name="innerRadius"/> (enter set) and <paramref name="outer"/> with slots within
    /// <paramref name="outerRadius"/> (keep/exit set). Both destinations are cleared first. Each
    /// candidate's distance is computed exactly once.
    /// </summary>
    public void QueryRadiusWithHysteresis(
        float x, float y, float innerRadius, float outerRadius, SlotBitset inner, SlotBitset outer)
    {
        Debug.Assert(outerRadius >= innerRadius, "outer radius must be >= inner radius");
        inner.Clear();
        outer.Clear();
        QueryCore(x, y, innerRadius, outerRadius, inner, outer);
    }

    private void QueryCore(float x, float y, float innerRadius, float outerRadius, SlotBitset inner, SlotBitset outer)
    {
        if (_entryCount == 0)
        {
            return;
        }

        // A non-finite focus (never set, or garbage) sees nothing rather than crashing.
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            return;
        }

        float innerSq = innerRadius * innerRadius;
        float outerSq = outerRadius * outerRadius;

        int minCx = CellCoord(x - outerRadius);
        int maxCx = CellCoord(x + outerRadius);
        int minCy = CellCoord(y - outerRadius);
        int maxCy = CellCoord(y + outerRadius);

        for (int cy = minCy; cy <= maxCy; cy++)
        {
            for (int cx = minCx; cx <= maxCx; cx++)
            {
                int bucket = (int)(HashCell(cx, cy) & (uint)_bucketMask);
                int end = _starts[bucket + 1];
                for (int e = _starts[bucket]; e < end; e++)
                {
                    // Exact circle test. Aliased entries from other cells either fail it or were
                    // in radius anyway; duplicate visits just re-set the same bit.
                    float dx = _entryX[e] - x;
                    float dy = _entryY[e] - y;
                    float distSq = dx * dx + dy * dy;
                    if (distSq <= outerSq)
                    {
                        int slot = _entrySlot[e];
                        outer.Set(slot);
                        if (distSq <= innerSq)
                        {
                            inner.Set(slot);
                        }
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellCoord(float v)
        // (int) of NaN/±Inf saturates (NaN -> 0) instead of throwing; such coords hash somewhere
        // harmless and the exact distance check keeps the entity invisible.
        => (int)MathF.Floor(v * _invCellSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BucketOf(float x, float y)
        => (int)(HashCell(CellCoord(x), CellCoord(y)) & (uint)_bucketMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashCell(int cx, int cy)
    {
        // 2D integer hash: multiply each coordinate by a large odd constant, mix. Negative
        // coordinates wrap through uint arithmetic — entities far outside expected bounds are fine.
        uint h = (uint)cx * 0x9E3779B1u;
        h ^= (uint)cy * 0x85EBCA77u;
        h ^= h >> 16;
        return h;
    }
}
