namespace Pix3.Rooms.Protocol;

/// <summary>
/// The canonical mutable entity state on the wire. A plain struct with public fields: it is copied
/// per entity per tick, so it is always passed by <c>in</c> or <c>ref</c> and never boxed.
/// </summary>
/// <remarks>
/// <see cref="Kind"/> and <see cref="OwnerId"/> travel only in a <c>FullRecord</c> (snapshot / AOI
/// enter). A <c>DeltaRecord</c> carries the masked subset of X, Y, Rot, Vx, Vy, Flags.
/// </remarks>
public struct EntityWireState
{
    /// <summary>World X.</summary>
    public float X;

    /// <summary>World Y.</summary>
    public float Y;

    /// <summary>Rotation in radians.</summary>
    public float Rot;

    /// <summary>Linear velocity along X, in units per second. Used by the client for extrapolation.</summary>
    public float Vx;

    /// <summary>Linear velocity along Y, in units per second.</summary>
    public float Vy;

    /// <summary>Application-defined entity kind. Opaque to this server. Full records only.</summary>
    public ushort Kind;

    /// <summary>ClientId of the owner (0 = server-owned). Full records only.</summary>
    public uint OwnerId;

    /// <summary>Application-defined bit flags. Opaque to this server.</summary>
    public byte Flags;

    /// <summary>
    /// Copies from <paramref name="source"/> only the fields selected by <paramref name="mask"/>,
    /// leaving everything else untouched. This is the one place mask semantics are implemented, so
    /// server-side apply and client-side apply can never drift.
    /// </summary>
    /// <param name="mask">A <see cref="DeltaMask"/> combination. Signal bits are ignored here.</param>
    /// <param name="source">The decoded delta whose masked fields are authoritative.</param>
    public void Apply(byte mask, in EntityWireState source)
    {
        if ((mask & DeltaMask.X) != 0) X = source.X;
        if ((mask & DeltaMask.Y) != 0) Y = source.Y;
        if ((mask & DeltaMask.Rot) != 0) Rot = source.Rot;
        if ((mask & DeltaMask.Vx) != 0) Vx = source.Vx;
        if ((mask & DeltaMask.Vy) != 0) Vy = source.Vy;
        if ((mask & DeltaMask.Flags) != 0) Flags = source.Flags;
    }
}
