using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// Hand-packed codecs for the hot plane (TypeIds 67 / 68 / 69). Everything here is
/// <see cref="Span{T}"/>-based, little-endian and allocation-free, and every reader returns
/// <c>bool</c> — malformed or truncated input must never throw, because it arrives from the network.
/// </summary>
/// <remarks>
/// <para>
/// Writers return the number of bytes written, or <c>0</c> when the destination is too small, so a
/// caller can fill a frame until it stops fitting without ever computing sizes twice.
/// </para>
/// <para>
/// Frame writers are split into "header" and "section count" pieces on purpose: the room encoder
/// stamps a header, reserves a count slot with <see cref="WriteSectionCountPlaceholder"/>, appends
/// records while they fit, then patches the real count with <see cref="TryPatchSectionCount"/>.
/// This is what makes <i>encode-once, memcpy-many</i> fan-out possible.
/// </para>
/// </remarks>
public static class HotWire
{
    // ── Record sizes ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>FullRecord</c> is fixed: <c>u32 NetId</c>, <c>u16 Kind</c>, <c>u32 OwnerId</c>,
    /// <c>f32 X/Y/Rot/Vx/Vy</c>, <c>u8 Flags</c>.
    /// </summary>
    public const int FullRecordSize = 31;

    /// <summary>Smallest <c>DeltaRecord</c>: <c>u32 NetId</c> + <c>u8 Mask</c>, no payload.</summary>
    public const int MinDeltaRecordSize = 5;

    /// <summary>Largest <c>DeltaRecord</c>: header plus all five floats plus the flags byte.</summary>
    public const int MaxDeltaRecordSize = 26;

    /// <summary>Bytes a <c>u32 NetId</c> occupies in a DeltaFrame removed section.</summary>
    public const int RemovedIdSize = 4;

    /// <summary>Bytes a <c>u16</c> section count occupies.</summary>
    public const int SectionCountSize = 2;

    // ── Frame header geometry ─────────────────────────────────────────────────

    /// <summary><c>[u8 TypeId=68][u32 ServerTick][u16 Count]</c>.</summary>
    public const int SnapshotFrameHeaderSize = 7;

    /// <summary>Offset of the <c>u16 Count</c> slot inside a SnapshotFrame.</summary>
    public const int SnapshotFrameCountOffset = 5;

    /// <summary><c>[u8 TypeId=69][u32 ServerTick]</c>; the three section counts follow.</summary>
    public const int DeltaFrameHeaderSize = 5;

    /// <summary>Smallest well-formed DeltaFrame: header plus three empty section counts.</summary>
    public const int MinDeltaFrameSize = DeltaFrameHeaderSize + (SectionCountSize * 3);

    /// <summary><c>[u8 TypeId=67][u32 ClientTick][u8 Count]</c>.</summary>
    public const int EntityUpdateFrameHeaderSize = 6;

    /// <summary>Offset of the <c>u8 Count</c> slot inside an EntityUpdateFrame.</summary>
    public const int EntityUpdateFrameCountOffset = 5;

    /// <summary>Largest record count an EntityUpdateFrame can express (the count field is a byte).</summary>
    public const int MaxEntityUpdateRecords = byte.MaxValue;

    // ── Client mask policy ────────────────────────────────────────────────────

    /// <summary>
    /// The only mask bits a client may set: the six payload bits plus <see cref="DeltaMask.Teleport"/>.
    /// <see cref="DeltaMask.ColdDirty"/> is server-authored (it promises a follow-up
    /// <see cref="EntityColdPropsEvent"/>), so a client setting it is a protocol violation.
    /// </summary>
    public const byte ClientAllowedMaskBits = DeltaMask.PayloadBits | DeltaMask.Teleport;   // 0x3F | 0x80

    /// <summary>Bits a client may never set.</summary>
    public const byte ClientForbiddenMaskBits = unchecked((byte)~ClientAllowedMaskBits);    // 0x40

    /// <summary>
    /// True when every bit in <paramref name="mask"/> is client-settable. <see cref="DeltaMask.None"/>
    /// is legal (an empty, no-op record); rejecting no-ops is a quota decision, not a protocol one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsClientMaskLegal(byte mask) => (mask & ClientForbiddenMaskBits) == 0;

    // ── Records ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Encoded size of a <c>DeltaRecord</c> with this mask. <see cref="DeltaMask.ColdDirty"/> and
    /// <see cref="DeltaMask.Teleport"/> contribute no payload bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DeltaRecordSize(byte mask)
        => MinDeltaRecordSize
         + (BitOperations.PopCount((uint)(mask & DeltaMask.FloatFieldBits)) << 2)
         + ((mask & DeltaMask.Flags) != 0 ? 1 : 0);

    /// <summary>
    /// Writes one <c>FullRecord</c>. Returns <see cref="FullRecordSize"/>, or 0 if
    /// <paramref name="destination"/> is too small (nothing is written in that case).
    /// </summary>
    public static int WriteFullRecord(Span<byte> destination, uint netId, in EntityWireState state)
    {
        if (destination.Length < FullRecordSize)
        {
            return 0;
        }

        Span<byte> d = destination.Slice(0, FullRecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(d, netId);
        BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(4), state.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(6), state.OwnerId);
        BinaryPrimitives.WriteSingleLittleEndian(d.Slice(10), state.X);
        BinaryPrimitives.WriteSingleLittleEndian(d.Slice(14), state.Y);
        BinaryPrimitives.WriteSingleLittleEndian(d.Slice(18), state.Rot);
        BinaryPrimitives.WriteSingleLittleEndian(d.Slice(22), state.Vx);
        BinaryPrimitives.WriteSingleLittleEndian(d.Slice(26), state.Vy);
        d[30] = state.Flags;
        return FullRecordSize;
    }

    /// <summary>
    /// Reads one <c>FullRecord</c> from the start of <paramref name="source"/>. Extra trailing bytes
    /// are ignored. False when the span is shorter than <see cref="FullRecordSize"/>.
    /// </summary>
    public static bool TryReadFullRecord(ReadOnlySpan<byte> source, out uint netId, out EntityWireState state)
    {
        netId = 0;
        state = default;
        if (source.Length < FullRecordSize)
        {
            return false;
        }

        ReadOnlySpan<byte> s = source.Slice(0, FullRecordSize);
        netId = BinaryPrimitives.ReadUInt32LittleEndian(s);
        state.Kind = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(4));
        state.OwnerId = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(6));
        state.X = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(10));
        state.Y = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(14));
        state.Rot = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(18));
        state.Vx = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(22));
        state.Vy = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(26));
        state.Flags = s[30];
        return true;
    }

    /// <summary>
    /// Writes one <c>DeltaRecord</c>: <c>u32 NetId</c>, <c>u8 Mask</c>, then the masked fields in bit
    /// order. Returns the byte count, or 0 if <paramref name="destination"/> is too small.
    /// </summary>
    public static int WriteDeltaRecord(Span<byte> destination, uint netId, byte mask, in EntityWireState state)
    {
        int size = DeltaRecordSize(mask);
        if (destination.Length < size)
        {
            return 0;
        }

        Span<byte> d = destination.Slice(0, size);
        BinaryPrimitives.WriteUInt32LittleEndian(d, netId);
        d[4] = mask;

        int offset = MinDeltaRecordSize;
        if ((mask & DeltaMask.X) != 0)
        {
            BinaryPrimitives.WriteSingleLittleEndian(d.Slice(offset), state.X);
            offset += 4;
        }

        if ((mask & DeltaMask.Y) != 0)
        {
            BinaryPrimitives.WriteSingleLittleEndian(d.Slice(offset), state.Y);
            offset += 4;
        }

        if ((mask & DeltaMask.Rot) != 0)
        {
            BinaryPrimitives.WriteSingleLittleEndian(d.Slice(offset), state.Rot);
            offset += 4;
        }

        if ((mask & DeltaMask.Vx) != 0)
        {
            BinaryPrimitives.WriteSingleLittleEndian(d.Slice(offset), state.Vx);
            offset += 4;
        }

        if ((mask & DeltaMask.Vy) != 0)
        {
            BinaryPrimitives.WriteSingleLittleEndian(d.Slice(offset), state.Vy);
            offset += 4;
        }

        if ((mask & DeltaMask.Flags) != 0)
        {
            d[offset] = state.Flags;
        }

        return size;
    }

    /// <summary>
    /// Reads one <c>DeltaRecord</c> from the start of <paramref name="source"/>.
    /// Fields absent from the mask are left at zero — merge with
    /// <see cref="EntityWireState.Apply"/>, never assign wholesale.
    /// </summary>
    /// <param name="bytesRead">Size of the record just consumed; 0 when the read failed.</param>
    public static bool TryReadDeltaRecord(
        ReadOnlySpan<byte> source,
        out uint netId,
        out byte mask,
        out EntityWireState state,
        out int bytesRead)
    {
        netId = 0;
        mask = 0;
        state = default;
        bytesRead = 0;

        if (source.Length < MinDeltaRecordSize)
        {
            return false;
        }

        byte candidateMask = source[4];
        int size = DeltaRecordSize(candidateMask);
        if (source.Length < size)
        {
            return false;
        }

        ReadOnlySpan<byte> s = source.Slice(0, size);
        netId = BinaryPrimitives.ReadUInt32LittleEndian(s);
        mask = candidateMask;

        int offset = MinDeltaRecordSize;
        if ((candidateMask & DeltaMask.X) != 0)
        {
            state.X = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(offset));
            offset += 4;
        }

        if ((candidateMask & DeltaMask.Y) != 0)
        {
            state.Y = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(offset));
            offset += 4;
        }

        if ((candidateMask & DeltaMask.Rot) != 0)
        {
            state.Rot = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(offset));
            offset += 4;
        }

        if ((candidateMask & DeltaMask.Vx) != 0)
        {
            state.Vx = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(offset));
            offset += 4;
        }

        if ((candidateMask & DeltaMask.Vy) != 0)
        {
            state.Vy = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(offset));
            offset += 4;
        }

        if ((candidateMask & DeltaMask.Flags) != 0)
        {
            state.Flags = s[offset];
        }

        bytesRead = size;
        return true;
    }

    // ── Shared section primitives ─────────────────────────────────────────────

    /// <summary>
    /// Reserves a <c>u16</c> section-count slot, pre-filled with 0. Returns
    /// <see cref="SectionCountSize"/>, or 0 if the destination is too small. Remember the absolute
    /// offset of this slot and patch it with <see cref="TryPatchSectionCount"/> once the records are in.
    /// </summary>
    public static int WriteSectionCountPlaceholder(Span<byte> destination)
    {
        if (destination.Length < SectionCountSize)
        {
            return 0;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, 0);
        return SectionCountSize;
    }

    /// <summary>
    /// Overwrites a reserved <c>u16</c> count slot at <paramref name="countOffset"/> (absolute, from
    /// the start of the frame). False when the offset is outside the frame or the count does not fit
    /// in a <c>u16</c>.
    /// </summary>
    public static bool TryPatchSectionCount(Span<byte> frame, int countOffset, int count)
    {
        if ((uint)count > ushort.MaxValue)
        {
            return false;
        }

        if (countOffset < 0 || countOffset + SectionCountSize > frame.Length)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(countOffset), (ushort)count);
        return true;
    }

    /// <summary>
    /// Appends a removed <c>u32 NetId</c> to a DeltaFrame removed section. Returns
    /// <see cref="RemovedIdSize"/>, or 0 if the destination is too small.
    /// </summary>
    public static int WriteRemovedId(Span<byte> destination, uint netId)
    {
        if (destination.Length < RemovedIdSize)
        {
            return 0;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, netId);
        return RemovedIdSize;
    }

    /// <summary>
    /// Reads a bare <c>u32 NetId</c> from the start of <paramref name="source"/> (a removed-section
    /// entry). False when fewer than <see cref="RemovedIdSize"/> bytes are available.
    /// </summary>
    public static bool TryReadRemovedId(ReadOnlySpan<byte> source, out uint netId)
    {
        if (source.Length < RemovedIdSize)
        {
            netId = 0;
            return false;
        }

        netId = BinaryPrimitives.ReadUInt32LittleEndian(source);
        return true;
    }

    // ── SnapshotFrame (68, S→C) ───────────────────────────────────────────────

    /// <summary>
    /// Stamps <c>[u8 68][u32 ServerTick][u16 Count = 0]</c>. Returns
    /// <see cref="SnapshotFrameHeaderSize"/>, or 0 if the destination is too small. Append
    /// <c>FullRecord</c>s after it, then call <see cref="TryPatchSnapshotFrameCount"/>.
    /// </summary>
    public static int WriteSnapshotFrameHeader(Span<byte> destination, uint serverTick)
    {
        if (destination.Length < SnapshotFrameHeaderSize)
        {
            return 0;
        }

        destination[0] = MessageTypeIds.SnapshotFrame;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(1), serverTick);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(SnapshotFrameCountOffset), 0);
        return SnapshotFrameHeaderSize;
    }

    /// <summary>Patches the record count of a SnapshotFrame written into <paramref name="frame"/>.</summary>
    public static bool TryPatchSnapshotFrameCount(Span<byte> frame, int count)
        => TryPatchSectionCount(frame, SnapshotFrameCountOffset, count);

    /// <summary>
    /// Validates a complete SnapshotFrame (TypeId included) and hands back the record block.
    /// False on a wrong TypeId, a short header, or a count the buffer cannot hold.
    /// </summary>
    /// <param name="records">Exactly <paramref name="count"/> × <see cref="FullRecordSize"/> bytes.</param>
    public static bool TryReadSnapshotFrame(
        ReadOnlySpan<byte> frame,
        out uint serverTick,
        out int count,
        out ReadOnlySpan<byte> records)
    {
        serverTick = 0;
        count = 0;
        records = default;

        if (frame.Length < SnapshotFrameHeaderSize || frame[0] != MessageTypeIds.SnapshotFrame)
        {
            return false;
        }

        int declared = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(SnapshotFrameCountOffset));
        int payloadBytes = declared * FullRecordSize;
        if (frame.Length - SnapshotFrameHeaderSize < payloadBytes)
        {
            return false;
        }

        serverTick = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(1));
        count = declared;
        records = frame.Slice(SnapshotFrameHeaderSize, payloadBytes);
        return true;
    }

    // ── DeltaFrame (69, S→C) ──────────────────────────────────────────────────

    /// <summary>
    /// Stamps <c>[u8 69][u32 ServerTick]</c>. Returns <see cref="DeltaFrameHeaderSize"/>, or 0 if the
    /// destination is too small. Then, in order: a removed section, an enter section and an update
    /// section — each opened with <see cref="WriteSectionCountPlaceholder"/> and closed with
    /// <see cref="TryPatchSectionCount"/>. All three sections must be present even when empty.
    /// </summary>
    public static int WriteDeltaFrameHeader(Span<byte> destination, uint serverTick)
    {
        if (destination.Length < DeltaFrameHeaderSize)
        {
            return 0;
        }

        destination[0] = MessageTypeIds.DeltaFrame;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(1), serverTick);
        return DeltaFrameHeaderSize;
    }

    /// <summary>
    /// Validates a complete DeltaFrame (TypeId included) and splits it into its three sections.
    /// The removed and enter sections are checked byte-exactly; the update section is checked against
    /// its minimum size (records are variable length) and is then walked with
    /// <see cref="DeltaFrameSections.TryReadNextUpdate"/>, which validates each record in turn.
    /// </summary>
    public static bool TryReadDeltaFrame(ReadOnlySpan<byte> frame, out DeltaFrameSections sections)
    {
        sections = default;

        if (frame.Length < MinDeltaFrameSize || frame[0] != MessageTypeIds.DeltaFrame)
        {
            return false;
        }

        uint serverTick = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(1));
        int offset = DeltaFrameHeaderSize;

        int removedCount = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset));
        offset += SectionCountSize;
        int removedBytes = removedCount * RemovedIdSize;
        if (frame.Length - offset < removedBytes)
        {
            return false;
        }

        ReadOnlySpan<byte> removed = frame.Slice(offset, removedBytes);
        offset += removedBytes;

        if (frame.Length - offset < SectionCountSize)
        {
            return false;
        }

        int enterCount = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset));
        offset += SectionCountSize;
        int enterBytes = enterCount * FullRecordSize;
        if (frame.Length - offset < enterBytes)
        {
            return false;
        }

        ReadOnlySpan<byte> enter = frame.Slice(offset, enterBytes);
        offset += enterBytes;

        if (frame.Length - offset < SectionCountSize)
        {
            return false;
        }

        int updateCount = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset));
        offset += SectionCountSize;
        int updateBytes = frame.Length - offset;
        if (updateBytes < updateCount * MinDeltaRecordSize)
        {
            return false;
        }

        sections = new DeltaFrameSections(
            serverTick,
            removedCount,
            removed,
            enterCount,
            enter,
            updateCount,
            frame.Slice(offset, updateBytes));
        return true;
    }

    // ── EntityUpdateFrame (67, C→S) ───────────────────────────────────────────

    /// <summary>
    /// Stamps <c>[u8 67][u32 ClientTick][u8 Count = 0]</c>. Returns
    /// <see cref="EntityUpdateFrameHeaderSize"/>, or 0 if the destination is too small. Append
    /// <c>DeltaRecord</c>s, then call <see cref="TryPatchEntityUpdateFrameCount"/>.
    /// </summary>
    public static int WriteEntityUpdateFrameHeader(Span<byte> destination, uint clientTick)
    {
        if (destination.Length < EntityUpdateFrameHeaderSize)
        {
            return 0;
        }

        destination[0] = MessageTypeIds.EntityUpdateFrame;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(1), clientTick);
        destination[EntityUpdateFrameCountOffset] = 0;
        return EntityUpdateFrameHeaderSize;
    }

    /// <summary>
    /// Patches the <c>u8</c> record count of an EntityUpdateFrame. False when the frame is too short
    /// or the count exceeds <see cref="MaxEntityUpdateRecords"/>.
    /// </summary>
    public static bool TryPatchEntityUpdateFrameCount(Span<byte> frame, int count)
    {
        if ((uint)count > MaxEntityUpdateRecords || frame.Length <= EntityUpdateFrameCountOffset)
        {
            return false;
        }

        frame[EntityUpdateFrameCountOffset] = (byte)count;
        return true;
    }

    /// <summary>
    /// Validates a complete EntityUpdateFrame (TypeId included) and hands back the record block.
    /// Records are variable length, so only the minimum size is checked here; walk
    /// <paramref name="records"/> with <see cref="TryReadDeltaRecord"/>, which validates each record
    /// and reports how many bytes it consumed.
    /// </summary>
    /// <remarks>
    /// <paramref name="clientTick"/> is advisory: the server stamps its own tick and must never trust
    /// this value for ordering decisions that affect other clients.
    /// </remarks>
    public static bool TryReadEntityUpdateFrame(
        ReadOnlySpan<byte> frame,
        out uint clientTick,
        out int count,
        out ReadOnlySpan<byte> records)
    {
        clientTick = 0;
        count = 0;
        records = default;

        if (frame.Length < EntityUpdateFrameHeaderSize || frame[0] != MessageTypeIds.EntityUpdateFrame)
        {
            return false;
        }

        int declared = frame[EntityUpdateFrameCountOffset];
        int available = frame.Length - EntityUpdateFrameHeaderSize;
        if (available < declared * MinDeltaRecordSize)
        {
            return false;
        }

        clientTick = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(1));
        count = declared;
        records = frame.Slice(EntityUpdateFrameHeaderSize, available);
        return true;
    }
}
