namespace Pix3.Rooms.Protocol;

/// <summary>
/// Bit flags describing which fields a <c>DeltaRecord</c> carries. Deliberately <see cref="byte"/>
/// constants rather than a <c>[Flags]</c> enum: the mask is read and written on the hot path and
/// must never require a boxing or conversion step.
/// </summary>
/// <remarks>
/// Payload fields appear in the record in bit order (X, Y, Rot, Vx, Vy, Flags).
/// <see cref="ColdDirty"/> and <see cref="Teleport"/> carry no payload bytes.
/// </remarks>
public static class DeltaMask
{
    /// <summary>Nothing changed. A legal but empty record (5 bytes, header only).</summary>
    public const byte None = 0x00;

    /// <summary><c>f32 X</c> present.</summary>
    public const byte X = 0x01;

    /// <summary><c>f32 Y</c> present.</summary>
    public const byte Y = 0x02;

    /// <summary><c>f32 Rot</c> present.</summary>
    public const byte Rot = 0x04;

    /// <summary><c>f32 Vx</c> present.</summary>
    public const byte Vx = 0x08;

    /// <summary><c>f32 Vy</c> present.</summary>
    public const byte Vy = 0x10;

    /// <summary><c>u8 Flags</c> present.</summary>
    public const byte Flags = 0x20;

    /// <summary>Cold props changed; the client should expect an <see cref="EntityColdPropsEvent"/>. No payload bytes.</summary>
    public const byte ColdDirty = 0x40;

    /// <summary>Discontinuity — the receiver must snap instead of interpolating. No payload bytes.</summary>
    public const byte Teleport = 0x80;

    /// <summary>The five <c>f32</c> payload bits (X, Y, Rot, Vx, Vy).</summary>
    public const byte FloatFieldBits = X | Y | Rot | Vx | Vy;   // 0x1F

    /// <summary>All bits that contribute payload bytes.</summary>
    public const byte PayloadBits = FloatFieldBits | Flags;     // 0x3F

    /// <summary>Bits that are pure signals and contribute no payload bytes.</summary>
    public const byte SignalBits = ColdDirty | Teleport;        // 0xC0
}
