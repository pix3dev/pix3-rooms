using System.Runtime.CompilerServices;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// The <c>FrameFlags</c> byte at offset 7 of a <c>SnapshotPacket</c>. Deliberately <see cref="byte"/>
/// constants rather than a <c>[Flags]</c> enum: it is written on the hot path and must never require a
/// conversion step.
/// </summary>
/// <remarks>
/// A large snapshot is split across several self-contained frames and only the last carries
/// <see cref="Final"/>. Without that bit a client had no way to know a multi-frame snapshot was
/// complete. Whether a frame is the final one is only known <i>after</i> it has been filled, so the
/// byte is stamped as <see cref="None"/> by the header writer and patched afterwards with
/// <see cref="HotWire.TryPatchSnapshotPacketFrameFlags"/>.
/// </remarks>
public static class FrameFlags
{
    /// <summary>No flags. What the header writer stamps; also every non-final snapshot frame.</summary>
    public const byte None = 0x00;

    /// <summary>Bit 0 — this is the last frame of the snapshot; the client's known set is now complete.</summary>
    public const byte Final = 0x01;

    /// <summary>Bits 1–7. Reserved, sent as 0, ignored on receipt.</summary>
    public const byte ReservedBits = 0xFE;

    /// <summary>True when <see cref="Final"/> is set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFinal(byte frameFlags) => (frameFlags & Final) != 0;
}
