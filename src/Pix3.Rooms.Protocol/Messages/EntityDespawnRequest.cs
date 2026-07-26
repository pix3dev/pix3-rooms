using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.EntityDespawnRequest"/>. Only the owner may despawn an
/// entity; anyone else is refused with <c>RejectCode.NotEntityOwner</c>.
/// </summary>
[MemoryPackable]
public sealed partial class EntityDespawnRequest
{
    /// <summary>The entity to remove.</summary>
    public uint NetId { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public EntityDespawnRequest()
    {
    }
}
