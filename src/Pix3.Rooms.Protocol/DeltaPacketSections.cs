namespace Pix3.Rooms.Protocol;

/// <summary>
/// The validated view of one <c>DeltaPacket</c> produced by <see cref="HotWire.TryReadDeltaPacket"/>.
/// A <c>ref struct</c> so the sections stay slices of the receive buffer — decoding a packet allocates
/// nothing.
/// </summary>
/// <remarks>
/// Sections must be applied in wire order: <b>removals first</b>, then enters, then updates. That is
/// what makes slot addressing safe on an ordered stream — a slot's removal always precedes any reuse
/// of it.
/// </remarks>
public readonly ref struct DeltaPacketSections
{
    /// <summary>
    /// Per-client sequence number, advanced only when a frame is actually emitted and wrapping mod
    /// 2¹⁶. A gap (<c>seq != last + 1</c>) means desync: send a <see cref="ResyncCommand"/> and ignore
    /// hot frames until the next snapshot.
    /// </summary>
    public readonly ushort Seq;

    /// <summary>Server tick the packet was produced on.</summary>
    public readonly uint ServerTick;

    /// <summary>Number of slots in <see cref="Removed"/>.</summary>
    public readonly int RemovedCount;

    /// <summary>
    /// Entities that were despawned <i>or</i> left the receiver's area of interest — from the client's
    /// point of view both mean "stop rendering this". Addressed by <c>u16 Slot</c>, so exactly
    /// <see cref="RemovedCount"/> × <see cref="HotWire.RemovedSlotSize"/> bytes. Resolve each slot
    /// through the client's own slot → netId table, learned from the <c>FullRecord</c> that introduced
    /// the entity.
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
    /// Already-known entities that changed, as variable-length slot-addressed <c>UpdateRecord</c>s.
    /// Walk it with <see cref="TryReadNextUpdate"/>.
    /// </summary>
    public readonly ReadOnlySpan<byte> Updates;

    /// <summary>Wraps three pre-validated sections. Callers use <see cref="HotWire.TryReadDeltaPacket"/>.</summary>
    public DeltaPacketSections(
        ushort seq,
        uint serverTick,
        int removedCount,
        ReadOnlySpan<byte> removed,
        int enterCount,
        ReadOnlySpan<byte> enter,
        int updateCount,
        ReadOnlySpan<byte> updates)
    {
        Seq = seq;
        ServerTick = serverTick;
        RemovedCount = removedCount;
        Removed = removed;
        EnterCount = enterCount;
        Enter = enter;
        UpdateCount = updateCount;
        Updates = updates;
    }

    /// <summary>True when the packet carries nothing at all (a conforming server never sends one).</summary>
    public bool IsEmpty => RemovedCount == 0 && EnterCount == 0 && UpdateCount == 0;

    /// <summary>Reads the removed slot at <paramref name="index"/>. False when out of range.</summary>
    public bool TryGetRemovedSlot(int index, out ushort slot)
    {
        slot = 0;
        if ((uint)index >= (uint)RemovedCount)
        {
            return false;
        }

        return HotWire.TryReadRemovedSlot(Removed.Slice(index * HotWire.RemovedSlotSize), out slot);
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
    /// Reads the next <c>UpdateRecord</c> from <see cref="Updates"/> and advances
    /// <paramref name="cursor"/>. Start at 0 and loop while this returns true; it also returns false
    /// on a truncated record, so a malformed packet simply stops the walk.
    /// </summary>
    public bool TryReadNextUpdate(ref int cursor, out ushort slot, out byte mask, out EntityWireState state)
    {
        slot = 0;
        mask = 0;
        state = default;
        if (cursor < 0 || cursor >= Updates.Length)
        {
            return false;
        }

        if (!HotWire.TryReadUpdateRecord(Updates.Slice(cursor), out slot, out mask, out state, out int read))
        {
            return false;
        }

        cursor += read;
        return true;
    }
}
