using System.Runtime.CompilerServices;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// Bit layout of the entity <c>Flags</c> byte, which travels in every <c>FullRecord</c> and is
/// maskable in updates via <see cref="DeltaMask.Flags"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the fabric took two of the eight bits.</b> In v1 the whole byte was app-defined, and the
/// fabric had exactly one rule for a departing owner: despawn everything it owned. That is wrong for
/// anything that is not an avatar — a host's pickups, spawners and world props vanished with it, and
/// host migration was impossible to express at all, so every public "play with friends" session died
/// the moment its creator backgrounded their phone. The policy has to be per entity, it has to be
/// declared by whoever spawned the entity, and it has to be visible to observers (an observer needs to
/// know whether a disappearing owner means a disappearing object). That makes it wire-affecting, which
/// is why it had to land in this version rather than being retrofitted later.
/// </para>
/// <para>
/// Bit 2 is reserved for the fabric's next need and costs nothing now: it must be sent as 0 and
/// ignored on receipt, so claiming it later is not a wire break.
/// </para>
/// <para>
/// Bits 3–7 stay app-defined and are replicated verbatim; the fabric never interprets them. Nothing
/// here allocates and every member is aggressively inlineable — the flags byte is touched per entity
/// per tick.
/// </para>
/// </remarks>
public static class EntityFlags
{
    /// <summary>Bits 0–1: the <see cref="OwnershipPolicy"/>.</summary>
    public const byte PolicyMask = 0b0000_0011;

    /// <summary>Bit 2: reserved for the fabric. Sent as 0, ignored on receipt.</summary>
    public const byte ReservedMask = 0b0000_0100;

    /// <summary>Bits 3–7: app-defined, replicated verbatim.</summary>
    public const byte AppMask = 0b1111_1000;

    /// <summary>Position of the lowest app-defined bit, for callers that want them right-aligned.</summary>
    public const int AppBitShift = 3;

    /// <summary>All bits the fabric owns (policy plus the reserved bit).</summary>
    public const byte FabricMask = PolicyMask | ReservedMask;

    /// <summary>Reads the ownership policy out of a flags byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OwnershipPolicy GetPolicy(byte flags) => (OwnershipPolicy)(flags & PolicyMask);

    /// <summary>
    /// Returns <paramref name="flags"/> with its policy bits replaced. The reserved bit and every
    /// app bit are preserved untouched.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte WithPolicy(byte flags, OwnershipPolicy policy)
        => (byte)((flags & ~PolicyMask) | ((byte)policy & PolicyMask));

    /// <summary>
    /// The app-defined bits, <b>masked in place</b> (still at bits 3–7) rather than shifted down, so
    /// <c>WithPolicy(AppBits(f), p)</c> rebuilds a valid flags byte. Shift by
    /// <see cref="AppBitShift"/> if a right-aligned value is wanted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte AppBits(byte flags) => (byte)(flags & AppMask);

    /// <summary>
    /// True when the fabric's reserved bit is clear, i.e. the byte is well-formed for this protocol
    /// version. A sender must satisfy this; a receiver treats a set bit as "ignore", never as fatal,
    /// which is what keeps claiming the bit later a non-breaking change.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsReservedBitClear(byte flags) => (flags & ReservedMask) == 0;
}
