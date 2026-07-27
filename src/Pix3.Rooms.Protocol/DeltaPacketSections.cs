namespace Pix3.Rooms.Protocol;

/// <summary>
/// The validated view of one DeltaFrame produced by <see cref="HotWire.TryReadDeltaFrame"/>.
/// A <c>ref struct</c> so the sections stay slices of the receive buffer — decoding a frame allocates
/// nothing.
/// </summary>
public readonly ref struct DeltaFrameSections
{
    /// <summary>Server tick the frame was produced on.</summary>
    public readonly uint ServerTick;

    /// <summary>Number of ids in <see cref="Removed"/>.</summary>
    public readonly int RemovedCount;

    /// <summary>
    /// Entities that were despawned <i>or</i> left the receiver's area of interest — from the client's
    /// point of view both mean "stop rendering this". Exactly <see cref="RemovedCount"/> ×
    /// <see cref="HotWire.RemovedIdSize"/> bytes.
    /// </summary>
    public readonly ReadOnlySpan<byte> Removed;

    /// <summary>Number of records in <see cref="Enter"/>.</summary>
    public readonly int EnterCount;

    /// <summary>
    /// Entities that entered the area of interest, as <c>FullRecord</c>s. Exactly
    /// <see cref="EnterCount"/> × <see cref="HotWire.FullRecordSize"/> bytes.
    /// </summary>
    public readonly ReadOnlySpan<byte> Enter;

    /// <summary>Number of records in <see cref="Updates"/>.</summary>
    public readonly int UpdateCount;

    /// <summary>
    /// Already-known entities that changed, as variable-length <c>DeltaRecord</c>s. Walk it with
    /// <see cref="TryReadNextUpdate"/>.
    /// </summary>
    public readonly ReadOnlySpan<byte> Updates;

    /// <summary>Wraps three pre-validated sections. Callers use <see cref="HotWire.TryReadDeltaFrame"/>.</summary>
    public DeltaFrameSections(
        uint serverTick,
        int removedCount,
        ReadOnlySpan<byte> removed,
        int enterCount,
        ReadOnlySpan<byte> enter,
        int updateCount,
        ReadOnlySpan<byte> updates)
    {
        ServerTick = serverTick;
        RemovedCount = removedCount;
        Removed = removed;
        EnterCount = enterCount;
        Enter = enter;
        UpdateCount = updateCount;
        Updates = updates;
    }

    /// <summary>True when the frame carries nothing at all (a conforming server never sends one).</summary>
    public bool IsEmpty => RemovedCount == 0 && EnterCount == 0 && UpdateCount == 0;

    /// <summary>Reads the removed net id at <paramref name="index"/>. False when out of range.</summary>
    public bool TryGetRemovedId(int index, out uint netId)
    {
        netId = 0;
        if ((uint)index >= (uint)RemovedCount)
        {
            return false;
        }

        return HotWire.TryReadRemovedId(Removed.Slice(index * HotWire.RemovedIdSize), out netId);
    }

    /// <summary>Reads the AOI-enter <c>FullRecord</c> at <paramref name="index"/>. False when out of range.</summary>
    public bool TryGetEnterRecord(int index, out uint netId, out EntityWireState state)
    {
        netId = 0;
        state = default;
        if ((uint)index >= (uint)EnterCount)
        {
            return false;
        }

        return HotWire.TryReadFullRecord(Enter.Slice(index * HotWire.FullRecordSize), out netId, out state);
    }

    /// <summary>
    /// Reads the next <c>DeltaRecord</c> from <see cref="Updates"/> and advances
    /// <paramref name="cursor"/>. Start at 0 and loop while this returns true; it also returns false
    /// on a truncated record, so a malformed frame simply stops the walk.
    /// </summary>
    public bool TryReadNextUpdate(ref int cursor, out uint netId, out byte mask, out EntityWireState state)
    {
        netId = 0;
        mask = 0;
        state = default;
        if (cursor < 0 || cursor >= Updates.Length)
        {
            return false;
        }

        if (!HotWire.TryReadDeltaRecord(Updates.Slice(cursor), out netId, out mask, out state, out int read))
        {
            return false;
        }

        cursor += read;
        return true;
    }
}
