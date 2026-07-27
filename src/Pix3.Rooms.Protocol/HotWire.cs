using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// Hand-packed codecs for the hot plane (TypeIds 67 / 68 / 69 / 130). Everything here is
/// <see cref="Span{T}"/>-based, little-endian and allocation-free, and every reader returns
/// <c>bool</c> — malformed or truncated input must never throw, because it arrives from the network.
/// </summary>
/// <remarks>
/// <para>
/// Writers return the number of bytes written, or <c>0</c> when the destination is too small, so a
/// caller can fill a frame until it stops fitting without ever computing sizes twice.
/// </para>
/// <para>
/// Packet writers are split into "header" and "section count" pieces on purpose: the room encoder
/// stamps a header, reserves a count slot with <see cref="WriteSectionCountPlaceholder"/>, appends
/// records while they fit, then patches the real count with <see cref="TryPatchSectionCount"/>.
/// This is what makes <i>encode-once, memcpy-many</i> fan-out possible.
/// </para>
/// <para>
/// The state fields moved here are the <b>quantized integers</b> from <see cref="EntityWireState"/>.
/// No codec in this file converts to or from <see cref="float"/>; that is
/// <see cref="WorldQuantizer"/>'s job, at the edges only.
/// </para>
/// <para>
/// Server→client records address entities by <c>u16 Slot</c>; client→server records address them by
/// <c>u32 NetId</c>, because the server needs the generation bits to reject a mutation aimed at a slot
/// that has since been reused. That asymmetry is the entire reason
/// <see cref="WriteUpdateRecord"/> and <see cref="WriteOwnerUpdateRecord"/> are two functions.
/// </para>
/// </remarks>
public static class HotWire
{
    // ── Record sizes ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>FullRecord</c> is fixed at 20 bytes: <c>u32 NetId</c>, <c>u16 Kind</c>, <c>u32 OwnerId</c>,
    /// <c>u16 QX</c>, <c>u16 QY</c>, <c>u8 QRot</c>, <c>i16 QVx</c>, <c>i16 QVy</c>, <c>u8 Flags</c>.
    /// </summary>
    public const int FullRecordSize = 20;

    /// <summary>Bytes before the masked payload of an <c>UpdateRecord</c>: <c>u16 Slot</c> + <c>u8 Mask</c>.</summary>
    public const int MinUpdateRecordSize = 3;

    /// <summary>Largest <c>UpdateRecord</c>: header plus every masked field.</summary>
    public const int MaxUpdateRecordSize = MinUpdateRecordSize + DeltaMask.MaxPayloadSize;      // 13

    /// <summary>Bytes before the masked payload of an <c>OwnerUpdateRecord</c>: <c>u32 NetId</c> + <c>u8 Mask</c>.</summary>
    public const int MinOwnerUpdateRecordSize = 5;

    /// <summary>Largest <c>OwnerUpdateRecord</c>: header plus every masked field.</summary>
    public const int MaxOwnerUpdateRecordSize = MinOwnerUpdateRecordSize + DeltaMask.MaxPayloadSize;   // 15

    /// <summary>Bytes a removal entry occupies in a <c>DeltaPacket</c> removed section: a bare <c>u16 Slot</c>.</summary>
    public const int RemovedSlotSize = 2;

    /// <summary>Bytes a <c>u16</c> section count occupies.</summary>
    public const int SectionCountSize = 2;

    // ── Packet header geometry ────────────────────────────────────────────────

    /// <summary><c>[u8 TypeId=68][u16 Seq][u32 ServerTick][u8 FrameFlags][u16 Count]</c>.</summary>
    public const int SnapshotPacketHeaderSize = 10;

    /// <summary>Offset of the <c>u8 FrameFlags</c> slot inside a <c>SnapshotPacket</c>.</summary>
    public const int SnapshotPacketFrameFlagsOffset = 7;

    /// <summary>Offset of the <c>u16 Count</c> slot inside a <c>SnapshotPacket</c>.</summary>
    public const int SnapshotPacketCountOffset = 8;

    /// <summary><c>[u8 TypeId=69][u16 Seq][u32 ServerTick]</c>; the three section counts follow.</summary>
    public const int DeltaPacketHeaderSize = 7;

    /// <summary>
    /// Bytes a <c>DeltaPacket</c> costs with all three sections empty: the header plus the three
    /// always-present <c>u16</c> counts. This — not <see cref="DeltaPacketHeaderSize"/> — is the header
    /// cost in the bandwidth budget, and it is also the smallest well-formed packet.
    /// </summary>
    public const int DeltaPacketFixedOverhead = DeltaPacketHeaderSize + (SectionCountSize * 3);   // 13

    /// <summary><c>[u8 TypeId=67][u32 ClientTick][u8 Count]</c>.</summary>
    public const int EntityUpdatePacketHeaderSize = 6;

    /// <summary>Offset of the <c>u8 Count</c> slot inside an <c>EntityUpdatePacket</c>.</summary>
    public const int EntityUpdatePacketCountOffset = 5;

    /// <summary>Largest record count an <c>EntityUpdatePacket</c> can express (the count field is a byte).</summary>
    public const int MaxEntityUpdateRecords = byte.MaxValue;

    /// <summary><c>[u8 TypeId=130][u16 Seq][u32 ServerTick][u8 Count]</c>.</summary>
    public const int SignalBatchPacketHeaderSize = 8;

    /// <summary>Offset of the <c>u8 Count</c> slot inside a <c>SignalBatchPacket</c>.</summary>
    public const int SignalBatchPacketCountOffset = 7;

    /// <summary>Largest entry count a <c>SignalBatchPacket</c> can express (the count field is a byte).</summary>
    public const int MaxSignalBatchEntries = byte.MaxValue;

    /// <summary>
    /// Fixed bytes of one <c>SignalBatchPacket</c> entry: <c>u32 SenderClientId</c> +
    /// <c>u8 NameLength</c> + <c>u8 PayloadLength</c>. The two variable blocks follow their lengths.
    /// </summary>
    public const int SignalEntryOverheadSize = 6;

    /// <summary>A signal name may not be empty.</summary>
    public const int MinSignalNameLength = 1;

    /// <summary>Longest signal name, in UTF-8 bytes, a batch entry can express.</summary>
    public const int MaxSignalNameLength = 64;

    /// <summary>
    /// Longest signal payload a batch entry can express. A larger payload is not eligible for the hot
    /// path at all and is refused with the <c>quota</c> counter: batched signals are small game events,
    /// not a data channel.
    /// </summary>
    public const int MaxSignalPayloadLength = 255;

    /// <summary>Smallest possible entry: overhead plus a one-byte name and an empty payload.</summary>
    public const int MinSignalEntrySize = SignalEntryOverheadSize + MinSignalNameLength;   // 7

    // ── Client mask policy ────────────────────────────────────────────────────

    /// <summary>
    /// The only mask bits a client may set: the six payload bits plus <see cref="DeltaMask.Teleport"/>.
    /// <see cref="DeltaMask.ColdDirty"/> is server-authored (it promises a follow-up
    /// <see cref="EntityPropsChangedEvent"/>), so a client setting it is a protocol violation.
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

    // ── FullRecord ────────────────────────────────────────────────────────────

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
        BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(10), state.QX);
        BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(12), state.QY);
        d[14] = state.QRot;
        BinaryPrimitives.WriteInt16LittleEndian(d.Slice(15), state.QVx);
        BinaryPrimitives.WriteInt16LittleEndian(d.Slice(17), state.QVy);
        d[19] = state.Flags;
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
        state.QX = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(10));
        state.QY = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(12));
        state.QRot = s[14];
        state.QVx = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(15));
        state.QVy = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(17));
        state.Flags = s[19];
        return true;
    }

    // ── UpdateRecord (S→C, slot-addressed) ────────────────────────────────────

    /// <summary>
    /// Encoded size of an <c>UpdateRecord</c> with this mask: 3 header bytes plus
    /// <see cref="DeltaMask.PayloadSize"/>. Between <see cref="MinUpdateRecordSize"/> and
    /// <see cref="MaxUpdateRecordSize"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int UpdateRecordSize(byte mask) => MinUpdateRecordSize + DeltaMask.PayloadSize(mask);

    /// <summary>
    /// Writes one <c>UpdateRecord</c>: <c>u16 Slot</c>, <c>u8 Mask</c>, then the masked fields in bit
    /// order. Returns the byte count, or 0 if <paramref name="destination"/> is too small.
    /// </summary>
    public static int WriteUpdateRecord(Span<byte> destination, ushort slot, byte mask, in EntityWireState state)
    {
        int size = UpdateRecordSize(mask);
        if (destination.Length < size)
        {
            return 0;
        }

        Span<byte> d = destination.Slice(0, size);
        BinaryPrimitives.WriteUInt16LittleEndian(d, slot);
        d[2] = mask;
        WriteMaskedFields(d, MinUpdateRecordSize, mask, in state);
        return size;
    }

    /// <summary>
    /// Reads one <c>UpdateRecord</c> from the start of <paramref name="source"/>.
    /// Fields absent from the mask are left at zero — merge with
    /// <see cref="EntityWireState.Apply"/>, never assign wholesale.
    /// </summary>
    /// <param name="bytesRead">Size of the record just consumed; 0 when the read failed.</param>
    public static bool TryReadUpdateRecord(
        ReadOnlySpan<byte> source,
        out ushort slot,
        out byte mask,
        out EntityWireState state,
        out int bytesRead)
    {
        slot = 0;
        mask = 0;
        state = default;
        bytesRead = 0;

        if (source.Length < MinUpdateRecordSize)
        {
            return false;
        }

        byte candidateMask = source[2];
        int size = UpdateRecordSize(candidateMask);
        if (source.Length < size)
        {
            return false;
        }

        ReadOnlySpan<byte> s = source.Slice(0, size);
        slot = BinaryPrimitives.ReadUInt16LittleEndian(s);
        mask = candidateMask;
        ReadMaskedFields(s, MinUpdateRecordSize, candidateMask, ref state);
        bytesRead = size;
        return true;
    }

    // ── OwnerUpdateRecord (C→S, netId-addressed) ──────────────────────────────

    /// <summary>
    /// Encoded size of an <c>OwnerUpdateRecord</c> with this mask: 5 header bytes plus
    /// <see cref="DeltaMask.PayloadSize"/>. Between <see cref="MinOwnerUpdateRecordSize"/> and
    /// <see cref="MaxOwnerUpdateRecordSize"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int OwnerUpdateRecordSize(byte mask) => MinOwnerUpdateRecordSize + DeltaMask.PayloadSize(mask);

    /// <summary>
    /// Writes one <c>OwnerUpdateRecord</c>: <c>u32 NetId</c>, <c>u8 Mask</c>, then the masked fields in
    /// bit order. Returns the byte count, or 0 if <paramref name="destination"/> is too small.
    /// </summary>
    public static int WriteOwnerUpdateRecord(Span<byte> destination, uint netId, byte mask, in EntityWireState state)
    {
        int size = OwnerUpdateRecordSize(mask);
        if (destination.Length < size)
        {
            return 0;
        }

        Span<byte> d = destination.Slice(0, size);
        BinaryPrimitives.WriteUInt32LittleEndian(d, netId);
        d[4] = mask;
        WriteMaskedFields(d, MinOwnerUpdateRecordSize, mask, in state);
        return size;
    }

    /// <summary>
    /// Reads one <c>OwnerUpdateRecord</c> from the start of <paramref name="source"/>. The caller still
    /// has to validate ownership, the generation bits of <paramref name="netId"/>, and
    /// <see cref="IsClientMaskLegal"/> — this only decodes.
    /// </summary>
    /// <param name="bytesRead">Size of the record just consumed; 0 when the read failed.</param>
    public static bool TryReadOwnerUpdateRecord(
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

        if (source.Length < MinOwnerUpdateRecordSize)
        {
            return false;
        }

        byte candidateMask = source[4];
        int size = OwnerUpdateRecordSize(candidateMask);
        if (source.Length < size)
        {
            return false;
        }

        ReadOnlySpan<byte> s = source.Slice(0, size);
        netId = BinaryPrimitives.ReadUInt32LittleEndian(s);
        mask = candidateMask;
        ReadMaskedFields(s, MinOwnerUpdateRecordSize, candidateMask, ref state);
        bytesRead = size;
        return true;
    }

    /// <summary>
    /// The masked payload, in bit order, shared by both update records — one implementation so the two
    /// layouts can never drift in anything but their header.
    /// </summary>
    private static void WriteMaskedFields(Span<byte> record, int offset, byte mask, in EntityWireState state)
    {
        if ((mask & DeltaMask.X) != 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(offset), state.QX);
            offset += 2;
        }

        if ((mask & DeltaMask.Y) != 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(offset), state.QY);
            offset += 2;
        }

        if ((mask & DeltaMask.Rot) != 0)
        {
            record[offset] = state.QRot;
            offset += 1;
        }

        if ((mask & DeltaMask.Vx) != 0)
        {
            BinaryPrimitives.WriteInt16LittleEndian(record.Slice(offset), state.QVx);
            offset += 2;
        }

        if ((mask & DeltaMask.Vy) != 0)
        {
            BinaryPrimitives.WriteInt16LittleEndian(record.Slice(offset), state.QVy);
            offset += 2;
        }

        if ((mask & DeltaMask.Flags) != 0)
        {
            record[offset] = state.Flags;
        }
    }

    /// <summary>Mirror of <see cref="WriteMaskedFields"/>; the record has already been length-checked.</summary>
    private static void ReadMaskedFields(ReadOnlySpan<byte> record, int offset, byte mask, ref EntityWireState state)
    {
        if ((mask & DeltaMask.X) != 0)
        {
            state.QX = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(offset));
            offset += 2;
        }

        if ((mask & DeltaMask.Y) != 0)
        {
            state.QY = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(offset));
            offset += 2;
        }

        if ((mask & DeltaMask.Rot) != 0)
        {
            state.QRot = record[offset];
            offset += 1;
        }

        if ((mask & DeltaMask.Vx) != 0)
        {
            state.QVx = BinaryPrimitives.ReadInt16LittleEndian(record.Slice(offset));
            offset += 2;
        }

        if ((mask & DeltaMask.Vy) != 0)
        {
            state.QVy = BinaryPrimitives.ReadInt16LittleEndian(record.Slice(offset));
            offset += 2;
        }

        if ((mask & DeltaMask.Flags) != 0)
        {
            state.Flags = record[offset];
        }
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
    /// Appends a removed <c>u16 Slot</c> to a <c>DeltaPacket</c> removed section. Returns
    /// <see cref="RemovedSlotSize"/>, or 0 if the destination is too small.
    /// </summary>
    public static int WriteRemovedSlot(Span<byte> destination, ushort slot)
    {
        if (destination.Length < RemovedSlotSize)
        {
            return 0;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, slot);
        return RemovedSlotSize;
    }

    /// <summary>
    /// Reads a bare <c>u16 Slot</c> from the start of <paramref name="source"/> (a removed-section
    /// entry). False when fewer than <see cref="RemovedSlotSize"/> bytes are available.
    /// </summary>
    public static bool TryReadRemovedSlot(ReadOnlySpan<byte> source, out ushort slot)
    {
        if (source.Length < RemovedSlotSize)
        {
            slot = 0;
            return false;
        }

        slot = BinaryPrimitives.ReadUInt16LittleEndian(source);
        return true;
    }

    // ── SnapshotPacket (68, S→C) ──────────────────────────────────────────────

    /// <summary>
    /// Stamps <c>[u8 68][u16 Seq][u32 ServerTick][u8 FrameFlags = None][u16 Count = 0]</c>. Returns
    /// <see cref="SnapshotPacketHeaderSize"/>, or 0 if the destination is too small. Append
    /// <c>FullRecord</c>s after it, then call <see cref="TryPatchSnapshotPacketCount"/> and — on the
    /// last frame of a split snapshot — <see cref="TryPatchSnapshotPacketFrameFlags"/>.
    /// </summary>
    /// <remarks>
    /// The flags byte starts at <see cref="FrameFlags.None"/> because whether a frame is the final one
    /// is only known after it has been filled, exactly like the record count.
    /// </remarks>
    public static int WriteSnapshotPacketHeader(Span<byte> destination, ushort seq, uint serverTick)
    {
        if (destination.Length < SnapshotPacketHeaderSize)
        {
            return 0;
        }

        destination[0] = MessageTypeIds.SnapshotPacket;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(1), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(3), serverTick);
        destination[SnapshotPacketFrameFlagsOffset] = FrameFlags.None;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(SnapshotPacketCountOffset), 0);
        return SnapshotPacketHeaderSize;
    }

    /// <summary>Patches the record count of a <c>SnapshotPacket</c> written into <paramref name="frame"/>.</summary>
    public static bool TryPatchSnapshotPacketCount(Span<byte> frame, int count)
        => TryPatchSectionCount(frame, SnapshotPacketCountOffset, count);

    /// <summary>
    /// Patches the <c>FrameFlags</c> byte of a <c>SnapshotPacket</c> — in practice to stamp
    /// <see cref="FrameFlags.Final"/> on the frame that carried the last records. False when the frame
    /// is too short.
    /// </summary>
    public static bool TryPatchSnapshotPacketFrameFlags(Span<byte> frame, byte frameFlags)
    {
        if (frame.Length <= SnapshotPacketFrameFlagsOffset)
        {
            return false;
        }

        frame[SnapshotPacketFrameFlagsOffset] = frameFlags;
        return true;
    }

    /// <summary>
    /// Validates a complete <c>SnapshotPacket</c> (TypeId included) and hands back the record block.
    /// False on a wrong TypeId, a short header, or a count the buffer cannot hold.
    /// </summary>
    /// <param name="records">Exactly <paramref name="count"/> × <see cref="FullRecordSize"/> bytes.</param>
    public static bool TryReadSnapshotPacket(
        ReadOnlySpan<byte> frame,
        out ushort seq,
        out uint serverTick,
        out byte frameFlags,
        out int count,
        out ReadOnlySpan<byte> records)
    {
        seq = 0;
        serverTick = 0;
        frameFlags = 0;
        count = 0;
        records = default;

        if (frame.Length < SnapshotPacketHeaderSize || frame[0] != MessageTypeIds.SnapshotPacket)
        {
            return false;
        }

        int declared = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(SnapshotPacketCountOffset));
        int payloadBytes = declared * FullRecordSize;
        if (frame.Length - SnapshotPacketHeaderSize < payloadBytes)
        {
            return false;
        }

        seq = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(1));
        serverTick = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(3));
        frameFlags = frame[SnapshotPacketFrameFlagsOffset];
        count = declared;
        records = frame.Slice(SnapshotPacketHeaderSize, payloadBytes);
        return true;
    }

    // ── DeltaPacket (69, S→C) ─────────────────────────────────────────────────

    /// <summary>
    /// Stamps <c>[u8 69][u16 Seq][u32 ServerTick]</c>. Returns <see cref="DeltaPacketHeaderSize"/>, or 0
    /// if the destination is too small. Then, in order: a removed section, an enter section and an
    /// update section — each opened with <see cref="WriteSectionCountPlaceholder"/> and closed with
    /// <see cref="TryPatchSectionCount"/>. All three sections must be present even when empty, which is
    /// why the packet's real fixed cost is <see cref="DeltaPacketFixedOverhead"/>.
    /// </summary>
    public static int WriteDeltaPacketHeader(Span<byte> destination, ushort seq, uint serverTick)
    {
        if (destination.Length < DeltaPacketHeaderSize)
        {
            return 0;
        }

        destination[0] = MessageTypeIds.DeltaPacket;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(1), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(3), serverTick);
        return DeltaPacketHeaderSize;
    }

    /// <summary>
    /// Validates a complete <c>DeltaPacket</c> (TypeId included) and splits it into its three sections.
    /// The removed and enter sections are checked byte-exactly; the update section is checked against
    /// its minimum size (records are variable length) and is then walked with
    /// <see cref="DeltaPacketSections.TryReadNextUpdate"/>, which validates each record in turn.
    /// </summary>
    public static bool TryReadDeltaPacket(ReadOnlySpan<byte> frame, out DeltaPacketSections sections)
    {
        sections = default;

        if (frame.Length < DeltaPacketFixedOverhead || frame[0] != MessageTypeIds.DeltaPacket)
        {
            return false;
        }

        ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(1));
        uint serverTick = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(3));
        int offset = DeltaPacketHeaderSize;

        int removedCount = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(offset));
        offset += SectionCountSize;
        int removedBytes = removedCount * RemovedSlotSize;
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
        if (updateBytes < updateCount * MinUpdateRecordSize)
        {
            return false;
        }

        sections = new DeltaPacketSections(
            seq,
            serverTick,
            removedCount,
            removed,
            enterCount,
            enter,
            updateCount,
            frame.Slice(offset, updateBytes));
        return true;
    }

    // ── EntityUpdatePacket (67, C→S) ──────────────────────────────────────────

    /// <summary>
    /// Stamps <c>[u8 67][u32 ClientTick][u8 Count = 0]</c>. Returns
    /// <see cref="EntityUpdatePacketHeaderSize"/>, or 0 if the destination is too small. Append
    /// <c>OwnerUpdateRecord</c>s, then call <see cref="TryPatchEntityUpdatePacketCount"/>.
    /// </summary>
    public static int WriteEntityUpdatePacketHeader(Span<byte> destination, uint clientTick)
    {
        if (destination.Length < EntityUpdatePacketHeaderSize)
        {
            return 0;
        }

        destination[0] = MessageTypeIds.EntityUpdatePacket;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(1), clientTick);
        destination[EntityUpdatePacketCountOffset] = 0;
        return EntityUpdatePacketHeaderSize;
    }

    /// <summary>
    /// Patches the <c>u8</c> record count of an <c>EntityUpdatePacket</c>. False when the frame is too
    /// short or the count exceeds <see cref="MaxEntityUpdateRecords"/>.
    /// </summary>
    public static bool TryPatchEntityUpdatePacketCount(Span<byte> frame, int count)
    {
        if ((uint)count > MaxEntityUpdateRecords || frame.Length <= EntityUpdatePacketCountOffset)
        {
            return false;
        }

        frame[EntityUpdatePacketCountOffset] = (byte)count;
        return true;
    }

    /// <summary>
    /// Validates a complete <c>EntityUpdatePacket</c> (TypeId included) and hands back the record block.
    /// Records are variable length, so only the minimum size is checked here; walk
    /// <paramref name="records"/> with <see cref="TryReadOwnerUpdateRecord"/>, which validates each
    /// record and reports how many bytes it consumed.
    /// </summary>
    /// <remarks>
    /// <paramref name="clientTick"/> is advisory: the server stamps its own tick and must never trust
    /// this value for ordering decisions that affect other clients.
    /// </remarks>
    public static bool TryReadEntityUpdatePacket(
        ReadOnlySpan<byte> frame,
        out uint clientTick,
        out int count,
        out ReadOnlySpan<byte> records)
    {
        clientTick = 0;
        count = 0;
        records = default;

        if (frame.Length < EntityUpdatePacketHeaderSize || frame[0] != MessageTypeIds.EntityUpdatePacket)
        {
            return false;
        }

        int declared = frame[EntityUpdatePacketCountOffset];
        int available = frame.Length - EntityUpdatePacketHeaderSize;
        if (available < declared * MinOwnerUpdateRecordSize)
        {
            return false;
        }

        clientTick = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(1));
        count = declared;
        records = frame.Slice(EntityUpdatePacketHeaderSize, available);
        return true;
    }

    // ── SignalBatchPacket (130, S→C) ──────────────────────────────────────────

    /// <summary>
    /// Encoded size of one batch entry: <see cref="SignalEntryOverheadSize"/> plus the two variable
    /// blocks. Returns 0 when either length is outside its legal range, so a caller can use this as the
    /// eligibility test before renting space.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SignalEntrySize(int nameLength, int payloadLength)
    {
        if (nameLength < MinSignalNameLength || nameLength > MaxSignalNameLength)
        {
            return 0;
        }

        if ((uint)payloadLength > MaxSignalPayloadLength)
        {
            return 0;
        }

        return SignalEntryOverheadSize + nameLength + payloadLength;
    }

    /// <summary>
    /// Stamps <c>[u8 130][u16 Seq][u32 ServerTick][u8 Count = 0]</c>. Returns
    /// <see cref="SignalBatchPacketHeaderSize"/>, or 0 if the destination is too small. Append entries
    /// with <see cref="WriteSignalEntry"/>, then call <see cref="TryPatchSignalBatchPacketCount"/>.
    /// </summary>
    public static int WriteSignalBatchPacketHeader(Span<byte> destination, ushort seq, uint serverTick)
    {
        if (destination.Length < SignalBatchPacketHeaderSize)
        {
            return 0;
        }

        destination[0] = MessageTypeIds.SignalBatchPacket;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(1), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(3), serverTick);
        destination[SignalBatchPacketCountOffset] = 0;
        return SignalBatchPacketHeaderSize;
    }

    /// <summary>
    /// Patches the <c>u8</c> entry count of a <c>SignalBatchPacket</c>. False when the frame is too
    /// short or the count exceeds <see cref="MaxSignalBatchEntries"/>.
    /// </summary>
    public static bool TryPatchSignalBatchPacketCount(Span<byte> frame, int count)
    {
        if ((uint)count > MaxSignalBatchEntries || frame.Length <= SignalBatchPacketCountOffset)
        {
            return false;
        }

        frame[SignalBatchPacketCountOffset] = (byte)count;
        return true;
    }

    /// <summary>
    /// Writes one batch entry: <c>u32 SenderClientId</c>, <c>u8 NameLength</c>, the UTF-8 name,
    /// <c>u8 PayloadLength</c>, the payload. Returns the byte count, or 0 when the destination is too
    /// small <i>or</i> a length is illegal (empty/over-long name, payload above
    /// <see cref="MaxSignalPayloadLength"/>) — an over-large signal is simply not eligible for the hot
    /// path.
    /// </summary>
    public static int WriteSignalEntry(
        Span<byte> destination,
        uint senderClientId,
        ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> payload)
    {
        int size = SignalEntrySize(name.Length, payload.Length);
        if (size == 0 || destination.Length < size)
        {
            return 0;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, senderClientId);
        destination[4] = (byte)name.Length;
        name.CopyTo(destination.Slice(5, name.Length));

        int payloadLengthOffset = 5 + name.Length;
        destination[payloadLengthOffset] = (byte)payload.Length;
        payload.CopyTo(destination.Slice(payloadLengthOffset + 1, payload.Length));
        return size;
    }

    /// <summary>
    /// Reads one batch entry from the start of <paramref name="source"/>. False on a truncated entry, a
    /// zero <c>NameLength</c>, or a name longer than <see cref="MaxSignalNameLength"/> — malformed input
    /// is a normal event here and never throws.
    /// </summary>
    /// <param name="bytesRead">Size of the entry just consumed; 0 when the read failed.</param>
    public static bool TryReadSignalEntry(
        ReadOnlySpan<byte> source,
        out uint senderClientId,
        out ReadOnlySpan<byte> name,
        out ReadOnlySpan<byte> payload,
        out int bytesRead)
    {
        senderClientId = 0;
        name = default;
        payload = default;
        bytesRead = 0;

        if (source.Length < MinSignalEntrySize)
        {
            return false;
        }

        int nameLength = source[4];
        if (nameLength < MinSignalNameLength || nameLength > MaxSignalNameLength)
        {
            return false;
        }

        int payloadLengthOffset = 5 + nameLength;
        if (source.Length <= payloadLengthOffset)
        {
            return false;
        }

        int payloadLength = source[payloadLengthOffset];
        int size = SignalEntryOverheadSize + nameLength + payloadLength;
        if (source.Length < size)
        {
            return false;
        }

        senderClientId = BinaryPrimitives.ReadUInt32LittleEndian(source);
        name = source.Slice(5, nameLength);
        payload = source.Slice(payloadLengthOffset + 1, payloadLength);
        bytesRead = size;
        return true;
    }

    /// <summary>
    /// Validates a complete <c>SignalBatchPacket</c> (TypeId included) and hands back an entry walker.
    /// Entries are variable length, so only the aggregate minimum is checked here; each entry is
    /// validated as <see cref="SignalBatchSections.TryReadNextEntry"/> walks it.
    /// </summary>
    public static bool TryReadSignalBatchPacket(ReadOnlySpan<byte> frame, out SignalBatchSections sections)
    {
        sections = default;

        if (frame.Length < SignalBatchPacketHeaderSize || frame[0] != MessageTypeIds.SignalBatchPacket)
        {
            return false;
        }

        int declared = frame[SignalBatchPacketCountOffset];
        int available = frame.Length - SignalBatchPacketHeaderSize;
        if (available < declared * MinSignalEntrySize)
        {
            return false;
        }

        sections = new SignalBatchSections(
            BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(1)),
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(3)),
            declared,
            frame.Slice(SignalBatchPacketHeaderSize, available));
        return true;
    }
}
