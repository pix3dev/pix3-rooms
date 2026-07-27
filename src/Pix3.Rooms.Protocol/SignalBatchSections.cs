namespace Pix3.Rooms.Protocol;

/// <summary>
/// The validated view of one <c>SignalBatchPacket</c> produced by
/// <see cref="HotWire.TryReadSignalBatchPacket"/>. A <c>ref struct</c> so the name and payload of every
/// entry stay slices of the receive buffer — decoding a packet allocates nothing.
/// </summary>
/// <remarks>
/// This is the AOI-scoped signal path: one packet per client per tick, assembled with the same
/// encode-once/memcpy-many discipline as the delta and flushed alongside it, so a burst of small game
/// events (a shooter's fire events) never costs an extra socket send.
/// </remarks>
public readonly ref struct SignalBatchSections
{
    /// <summary>
    /// Per-client sequence number, shared with the other hot frames: advanced only when a frame is
    /// actually emitted, wrapping mod 2¹⁶. A gap means desync.
    /// </summary>
    public readonly ushort Seq;

    /// <summary>Server tick the packet was produced on.</summary>
    public readonly uint ServerTick;

    /// <summary>Number of entries the header declares. Walk them with <see cref="TryReadNextEntry"/>.</summary>
    public readonly int Count;

    /// <summary>
    /// The raw entry block, immediately after the 8-byte header. Variable-length entries, so the only
    /// aggregate check made up front is that it can hold <see cref="Count"/> minimal entries.
    /// </summary>
    public readonly ReadOnlySpan<byte> Entries;

    /// <summary>Wraps a pre-validated header. Callers use <see cref="HotWire.TryReadSignalBatchPacket"/>.</summary>
    public SignalBatchSections(ushort seq, uint serverTick, int count, ReadOnlySpan<byte> entries)
    {
        Seq = seq;
        ServerTick = serverTick;
        Count = count;
        Entries = entries;
    }

    /// <summary>True when the packet carries no entries (a conforming server never sends one).</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Reads the next entry and advances <paramref name="cursor"/>. Start at 0 and loop while this
    /// returns true, at most <see cref="Count"/> times. Returns false on a zero or over-long
    /// <c>NameLength</c> and on a truncated entry, so a malformed packet simply stops the walk — never
    /// an exception.
    /// </summary>
    public bool TryReadNextEntry(
        ref int cursor,
        out uint senderClientId,
        out ReadOnlySpan<byte> name,
        out ReadOnlySpan<byte> payload)
    {
        senderClientId = 0;
        name = default;
        payload = default;
        if (cursor < 0 || cursor >= Entries.Length)
        {
            return false;
        }

        if (!HotWire.TryReadSignalEntry(
                Entries.Slice(cursor),
                out senderClientId,
                out name,
                out payload,
                out int read))
        {
            return false;
        }

        cursor += read;
        return true;
    }
}
