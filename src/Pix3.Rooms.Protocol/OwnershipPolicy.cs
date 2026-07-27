namespace Pix3.Rooms.Protocol;

/// <summary>
/// What the fabric does with an entity when its owner leaves the room. Carried in bits 0–1 of the
/// entity <see cref="EntityFlags">flags byte</see>.
/// </summary>
public enum OwnershipPolicy : byte
{
    /// <summary>Despawned when its owner leaves. The default, and the right answer for avatars.</summary>
    Owned = 0,

    /// <summary>Reassigned to the newly promoted host when its owner leaves. World props, pickups, spawners.</summary>
    Shared = 1,

    /// <summary>Reassignable to any client, not just the host. Carryable objects.</summary>
    Transferable = 2,

    /// <summary>Reserved encoding. Never sent; treated as <see cref="Owned"/> if received.</summary>
    Reserved = 3,
}
