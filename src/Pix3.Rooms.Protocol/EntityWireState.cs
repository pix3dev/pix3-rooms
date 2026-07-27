namespace Pix3.Rooms.Protocol;

/// <summary>
/// The canonical mutable entity state on the wire, in its <b>quantized</b> form. A plain struct with
/// public fields: it is copied per entity per tick, so it is always passed by <c>in</c> or <c>ref</c>
/// and never boxed.
/// </summary>
/// <remarks>
/// <para>
/// <b>There are no floats here, on purpose.</b> The quantized integers <i>are</i> the replicated
/// values: the server stores dequantized-from-quantized state, an owning client renders its own entity
/// from the same dequantized values, and — the load-bearing part — <b>dirty detection compares these
/// integers</b>. Comparing floats would keep an idle entity dirty forever on sub-quantum noise, so a
/// float field on this struct would be a bug waiting to happen rather than a convenience.
/// </para>
/// <para>
/// Float conversion is <see cref="WorldQuantizer"/>'s job and happens only at the edges: a spawn
/// request arriving from a client, AOI/spatial maths that needs real distances, and client-side
/// rendering. <b>Never inside a codec.</b> A codec moves these integers verbatim.
/// </para>
/// <para>
/// <see cref="Kind"/> and <see cref="OwnerId"/> travel only in a <c>FullRecord</c> (snapshot / AOI
/// enter). An <c>UpdateRecord</c> or <c>OwnerUpdateRecord</c> carries the masked subset of QX, QY,
/// QRot, QVx, QVy, Flags.
/// </para>
/// </remarks>
public struct EntityWireState
{
    /// <summary>Quantized world X. See <see cref="WorldQuantizer.TryQuantizePosition"/>.</summary>
    public ushort QX;

    /// <summary>Quantized world Y. See <see cref="WorldQuantizer.TryQuantizePosition"/>.</summary>
    public ushort QY;

    /// <summary>Quantized rotation, 256 steps per turn. See <see cref="WorldQuantizer.TryQuantizeRotation"/>.</summary>
    public byte QRot;

    /// <summary>Quantized linear velocity along X, 1/8 u/s per step. Off the wire by default.</summary>
    public short QVx;

    /// <summary>Quantized linear velocity along Y, 1/8 u/s per step. Off the wire by default.</summary>
    public short QVy;

    /// <summary>Application-defined entity kind, indexing the build's prefab table. Full records only.</summary>
    public ushort Kind;

    /// <summary>ClientId of the owner (0 = server-owned, read-only to every client). Full records only.</summary>
    public uint OwnerId;

    /// <summary>Bit flags: ownership policy in bits 0–1, app bits in 3–7. See <see cref="EntityFlags"/>.</summary>
    public byte Flags;

    /// <summary>
    /// Copies from <paramref name="source"/> only the fields selected by <paramref name="mask"/>,
    /// leaving everything else untouched. This is the one place mask semantics are implemented, so
    /// server-side apply and client-side apply can never drift.
    /// </summary>
    /// <param name="mask">A <see cref="DeltaMask"/> combination. Signal bits are ignored here.</param>
    /// <param name="source">The decoded update whose masked fields are authoritative.</param>
    public void Apply(byte mask, in EntityWireState source)
    {
        if ((mask & DeltaMask.X) != 0) QX = source.QX;
        if ((mask & DeltaMask.Y) != 0) QY = source.QY;
        if ((mask & DeltaMask.Rot) != 0) QRot = source.QRot;
        if ((mask & DeltaMask.Vx) != 0) QVx = source.QVx;
        if ((mask & DeltaMask.Vy) != 0) QVy = source.QVy;
        if ((mask & DeltaMask.Flags) != 0) Flags = source.Flags;
    }
}
