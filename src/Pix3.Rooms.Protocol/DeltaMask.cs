using System.Numerics;
using System.Runtime.CompilerServices;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// Bit flags describing which fields an <c>UpdateRecord</c> (S→C) or <c>OwnerUpdateRecord</c> (C→S)
/// carries. Deliberately <see cref="byte"/> constants rather than a <c>[Flags]</c> enum: the mask is
/// read and written on the hot path and must never require a boxing or conversion step.
/// </summary>
/// <remarks>
/// <para>
/// Payload fields appear in the record in <b>bit order</b> (X, Y, Rot, Vx, Vy, Flags) and are the
/// quantized integers, not floats: <c>u16 QX</c>, <c>u16 QY</c>, <c>u8 QRot</c>, <c>i16 QVx</c>,
/// <c>i16 QVy</c>, <c>u8 Flags</c>. <see cref="ColdDirty"/> and <see cref="Teleport"/> carry no payload
/// bytes at all.
/// </para>
/// <para>
/// Velocity stays in this vocabulary but is off the wire by default: at 20 Hz, linear interpolation of
/// 2D sprites does not need it. A typical moving entity therefore costs 8 B on the wire
/// (<c>u16 Slot</c> + mask + QX + QY + QRot).
/// </para>
/// </remarks>
public static class DeltaMask
{
    /// <summary>Nothing changed. A legal but empty record (header only).</summary>
    public const byte None = 0x00;

    /// <summary><c>u16 QX</c> present.</summary>
    public const byte X = 0x01;

    /// <summary><c>u16 QY</c> present.</summary>
    public const byte Y = 0x02;

    /// <summary><c>u8 QRot</c> present.</summary>
    public const byte Rot = 0x04;

    /// <summary><c>i16 QVx</c> present.</summary>
    public const byte Vx = 0x08;

    /// <summary><c>i16 QVy</c> present.</summary>
    public const byte Vy = 0x10;

    /// <summary><c>u8 Flags</c> present.</summary>
    public const byte Flags = 0x20;

    /// <summary>Cold props changed; the client should expect an <see cref="EntityPropsChangedEvent"/>. No payload bytes.</summary>
    public const byte ColdDirty = 0x40;

    /// <summary>Discontinuity — the receiver must snap instead of interpolating. No payload bytes.</summary>
    public const byte Teleport = 0x80;

    /// <summary>Bits whose payload is two bytes wide: <see cref="X"/>, <see cref="Y"/>, <see cref="Vx"/>, <see cref="Vy"/>.</summary>
    public const byte TwoByteFieldBits = X | Y | Vx | Vy;       // 0x1B

    /// <summary>Bits whose payload is one byte wide: <see cref="Rot"/>, <see cref="Flags"/>.</summary>
    public const byte OneByteFieldBits = Rot | Flags;           // 0x24

    /// <summary>All bits that contribute payload bytes.</summary>
    public const byte PayloadBits = TwoByteFieldBits | OneByteFieldBits;   // 0x3F

    /// <summary>Bits that are pure signals and contribute no payload bytes.</summary>
    public const byte SignalBits = ColdDirty | Teleport;        // 0xC0

    /// <summary>Payload bytes a record with every field present carries (2 × 4 + 1 × 2).</summary>
    public const int MaxPayloadSize = 10;

    /// <summary>
    /// Bytes the masked fields occupy, excluding the record header:
    /// <c>2 × popcount(mask &amp; (X|Y|Vx|Vy)) + (Rot ? 1 : 0) + (Flags ? 1 : 0)</c>.
    /// Signal bits contribute nothing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PayloadSize(byte mask)
        => (BitOperations.PopCount((uint)(mask & TwoByteFieldBits)) << 1)
         + BitOperations.PopCount((uint)(mask & OneByteFieldBits));
}
