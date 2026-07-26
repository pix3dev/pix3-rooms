using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.EntityColdPropsEvent"/>. Delivers an entity's cold props to
/// the subscribers that can see it — on AOI entry and after every change.
/// </summary>
[MemoryPackable]
public sealed partial class EntityColdPropsEvent
{
    /// <summary>The entity the blob belongs to.</summary>
    public uint NetId { get; set; }

    /// <summary>Opaque payload, byte-for-byte as the owner set it.</summary>
    public byte[] Json { get; set; } = [];

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public EntityColdPropsEvent()
    {
    }
}
