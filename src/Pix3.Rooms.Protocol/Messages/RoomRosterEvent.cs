using System.Text;
using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.RoomRosterEvent"/>. The room's <b>complete</b> membership —
/// <b>including the receiving client itself</b> — sent on every join and every resume.
/// <see cref="ClientIds"/> and <see cref="DisplayNames"/> are parallel arrays of equal length.
/// </summary>
/// <remarks>
/// <para>
/// A joiner is deliberately excluded from its own <c>PeerJoinedEvent</c> fan-out, so without this
/// message it would learn a room's population only as people came and went. Replaying
/// <see cref="PeerJoinedEvent"/> at the joiner is not the fix: the control lane is 64 frames deep and a
/// full control lane <b>closes the connection</b>, so a replay would kill a fresh session in a large
/// room — and it could never heal a <i>leave</i> the client missed during a resume grace either.
/// Appending the roster to <see cref="WelcomeEvent"/> is not the fix either: that is a single
/// unsplittable frame under the 4 KiB payload cap, which a roster bursts at a few dozen players.
/// </para>
/// <para>
/// It is a <b>full-state</b> message, not a delta: a receiver replaces its roster with it, exactly as
/// it replaces its known set with a snapshot. Room-scoped and never AOI-scoped, matching
/// <see cref="PeerJoinedEvent"/> and <see cref="PeerLeftEvent"/> — membership is not a spatial fact.
/// </para>
/// <para>
/// <b>Chunked.</b> A full roster does not always fit one frame: 600 members with 32-character display
/// names burst <see cref="MaxPayloadBytes"/> several times over. It is therefore split across several
/// self-contained <c>RoomRosterEvent</c>s and only the last carries bit 0 of
/// <see cref="FrameFlags"/> — the same discipline a multi-frame <c>SnapshotPacket</c> uses. One chunk
/// is always sent, even when the roster would be empty, so a client is never left waiting for a
/// completion it is never told about.
/// </para>
/// </remarks>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class RoomRosterEvent
{
    /// <summary>
    /// The control-frame ceiling a chunk must respect: 4 KiB, the protocol's <c>MaxPayloadBytes</c>
    /// frame-size invariant.
    /// </summary>
    /// <remarks>
    /// Stated here rather than read from the transport's quota options because it is the <i>wire</i>
    /// contract that bounds a chunk: "the server splits its own oversized snapshots rather than
    /// exceeding either" cap, and the same obligation applies to this message.
    /// </remarks>
    public const int MaxPayloadBytes = 4096;

    /// <summary>Bytes the empty <c>[i32 count]</c> collection header of an array occupies.</summary>
    private const int CollectionHeaderSize = 4;

    /// <summary>Client ids in this chunk, positionally paired with <see cref="DisplayNames"/>.</summary>
    [MemoryPackOrder(0)]
    public uint[] ClientIds { get; set; } = [];

    /// <summary>Display names in this chunk, positionally paired with <see cref="ClientIds"/>.</summary>
    [MemoryPackOrder(1)]
    public string[] DisplayNames { get; set; } = [];

    /// <summary>
    /// Bit 0 (<c>Final</c>) marks the last chunk: the receiver's roster is complete only once it has
    /// seen it. Bits 1–7 are reserved, sent as 0 and ignored on receipt. Use the
    /// <see cref="Protocol.FrameFlags"/> helpers to read and write it — it is the same byte
    /// <c>SnapshotPacket</c> carries, for the same reason.
    /// </summary>
    [MemoryPackOrder(2)]
    public byte FrameFlags { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public RoomRosterEvent()
    {
    }

    /// <summary>
    /// Bytes one display name occupies inside <see cref="DisplayNames"/>: the MemoryPack UTF-8 string
    /// form <c>[i32 ~utf8ByteCount][i32 utf16Length][utf8 bytes]</c>, or the bare 4-byte header a null
    /// or empty string collapses to.
    /// </summary>
    /// <param name="displayName">The name to measure.</param>
    /// <remarks>
    /// Counting UTF-8 <b>bytes</b> rather than characters is the whole point: a 32-character name is up
    /// to 128 bytes on the wire, and a chunk sized by character count would overflow the frame cap.
    /// </remarks>
    public static int EncodedDisplayNameSize(string? displayName)
        => string.IsNullOrEmpty(displayName)
            ? CollectionHeaderSize
            : (2 * sizeof(int)) + Encoding.UTF8.GetByteCount(displayName);

    /// <summary>Bytes <see cref="ClientIds"/> occupies for <paramref name="entryCount"/> entries.</summary>
    /// <param name="entryCount">Members in the chunk.</param>
    public static int EncodedClientIdsSize(int entryCount)
        => CollectionHeaderSize + (entryCount * sizeof(uint));

    /// <summary>Bytes an empty <see cref="DisplayNames"/> occupies; add one name at a time to it.</summary>
    public static int EmptyDisplayNamesSize => CollectionHeaderSize;

    /// <summary>
    /// The exact size of the complete frame — <c>[u8 TypeId][payload]</c> — that a chunk of
    /// <paramref name="entryCount"/> members whose names encode to <paramref name="displayNamesSize"/>
    /// bytes would produce. Compare it against <see cref="MaxPayloadBytes"/> to decide where a chunk
    /// ends; never guess an entry count.
    /// </summary>
    /// <param name="entryCount">Members in the chunk.</param>
    /// <param name="displayNamesSize">
    /// <see cref="EmptyDisplayNamesSize"/> plus one <see cref="EncodedDisplayNameSize"/> per member.
    /// </param>
    public static int EncodedFrameSize(int entryCount, int displayNamesSize)
    {
        int clientIdsSize = EncodedClientIdsSize(entryCount);

        return 1                                    // [u8 TypeId]
             + 1                                    // [u8 memberCount]
             + MemberLengthSize(clientIdsSize)      // ByteLength of ClientIds
             + MemberLengthSize(displayNamesSize)   // ByteLength of DisplayNames
             + 1                                    // ByteLength of FrameFlags: the raw byte 1
             + clientIdsSize
             + displayNamesSize
             + 1;                                   // the FrameFlags byte itself
    }

    /// <summary>
    /// Bytes a member's declared <c>ByteLength</c> varint costs: the raw byte for 0…127, otherwise the
    /// <c>0x84</c> marker plus a <c>u16</c>. The third form (<c>0x82</c> + <c>i32</c>) is unreachable
    /// here, because <see cref="MaxPayloadBytes"/> caps a frame far below 65536.
    /// </summary>
    private static int MemberLengthSize(int length) => length <= 127 ? 1 : 1 + sizeof(ushort);
}
