using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// Per-tick, encode-once storage for AOI-scoped signals. Each queued signal is written into one scratch
/// buffer exactly once as a complete <c>SignalBatchPacket</c> entry; per-recipient batches are assembled by
/// copying those byte ranges — the same discipline as entity deltas, for the same reason.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the sender's slot is stored.</b> An AOI signal is delivered to the peers who can see the
/// <i>sender</i>, so scoping needs the sender's focus entity slot, which is checked against each
/// recipient's visible set. A sender with no bound focus entity cannot be scoped at all, which is why
/// queueing refuses it rather than falling back to "everyone" — a 600× amplifier.
/// </para>
/// <para>
/// <b>Sizing.</b> Entries are capped at <see cref="HotWire.MaxSignalBatchEntries"/>, the largest count the
/// packet's <c>u8 Count</c> can express. The byte scratch is deliberately <i>not</i> sized for 255
/// <i>maximal</i> entries (that would be ~81 KB per room, ~21 MB across 256 rooms for a path whose
/// documented worst case is ~10 events per tick): it holds <see cref="ScratchBytes"/>, which fits several
/// hundred realistic entries, and queueing fails cleanly when either the entry cap or the byte cap is
/// reached. Both failures are reported the same way, and the caller counts them.
/// </para>
/// <para>Cleared once per tick and refilled; nothing here allocates after construction.</para>
/// </remarks>
public sealed class AoiSignalBuffer
{
    /// <summary>
    /// Bytes of per-tick signal scratch. 8 KiB holds ~390 typical entries (21 B each) — far past the
    /// documented worst case of ~10 events per tick, and past the 255-entry packet cap for anything but
    /// unusually large payloads.
    /// </summary>
    public const int ScratchBytes = 8 * 1024;

    private readonly byte[] _scratch = new byte[ScratchBytes];
    private readonly int[] _entryOffset = new int[HotWire.MaxSignalBatchEntries];
    private readonly int[] _entryLength = new int[HotWire.MaxSignalBatchEntries];
    private readonly int[] _senderSlot = new int[HotWire.MaxSignalBatchEntries];
    private readonly uint[] _senderClientId = new uint[HotWire.MaxSignalBatchEntries];

    private int _count;
    private int _cursor;

    /// <summary>Entries queued for the current tick.</summary>
    public int Count => _count;

    /// <summary>Scratch bytes consumed by the current tick's entries.</summary>
    public int BytesUsed => _cursor;

    /// <summary>Drops every entry. Called once per tick, before the room drains its inbound queue.</summary>
    public void Clear()
    {
        _count = 0;
        _cursor = 0;
    }

    /// <summary>
    /// Encodes one entry into the scratch buffer.
    /// </summary>
    /// <param name="senderClientId">The emitting client, replicated verbatim in the entry.</param>
    /// <param name="senderSlot">
    /// Slot of the sender's focus entity — the scoping key. Recipients that cannot see this slot do not
    /// receive the entry.
    /// </param>
    /// <returns>
    /// False when the name or payload length is outside what the wire format allows, or when this tick's
    /// batch is full (entry cap or scratch bytes). Nothing is written on failure.
    /// </returns>
    public bool TryAdd(uint senderClientId, int senderSlot, ReadOnlySpan<byte> name, ReadOnlySpan<byte> payload)
    {
        if (_count >= HotWire.MaxSignalBatchEntries)
        {
            return false;
        }

        // WriteSignalEntry validates both lengths and returns 0 rather than throwing, so an over-large
        // signal is simply not eligible for the hot path.
        int written = HotWire.WriteSignalEntry(
            _scratch.AsSpan(_cursor), senderClientId, name, payload);
        if (written == 0)
        {
            return false;
        }

        _entryOffset[_count] = _cursor;
        _entryLength[_count] = written;
        _senderSlot[_count] = senderSlot;
        _senderClientId[_count] = senderClientId;
        _count++;
        _cursor += written;
        return true;
    }

    /// <summary>Focus-entity slot of entry <paramref name="index"/> — the AOI scoping key.</summary>
    public int SenderSlotOf(int index) => _senderSlot[index];

    /// <summary>Emitting client of entry <paramref name="index"/>.</summary>
    public uint SenderClientIdOf(int index) => _senderClientId[index];

    /// <summary>The already-encoded bytes of entry <paramref name="index"/>, ready to be copied verbatim.</summary>
    public ReadOnlySpan<byte> EntryBytes(int index) => _scratch.AsSpan(_entryOffset[index], _entryLength[index]);
}
