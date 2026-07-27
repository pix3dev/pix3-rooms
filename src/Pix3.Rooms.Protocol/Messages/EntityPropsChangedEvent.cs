using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.EntityPropsChangedEvent"/>. Delivers an entity's cold props
/// to the subscribers that can see it: on AOI entry and after every change.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class EntityPropsChangedEvent
{
    /// <summary>The entity the blob belongs to.</summary>
    [MemoryPackOrder(0)]
    public uint NetId { get; set; }

    /// <summary>Opaque payload, byte-for-byte as the owner set it.</summary>
    [MemoryPackOrder(1)]
    public byte[] Json { get; set; } = [];

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public EntityPropsChangedEvent()
    {
    }
}
