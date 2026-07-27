using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.SetEntityPropsCommand"/>. Replaces the entity's
/// low-frequency opaque blob. Owner-only. Sets <see cref="DeltaMask.ColdDirty"/> for subscribers,
/// which then receive an <see cref="EntityPropsChangedEvent"/>.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class SetEntityPropsCommand
{
    /// <summary>Target entity.</summary>
    [MemoryPackOrder(0)]
    public uint NetId { get; set; }

    /// <summary>Opaque payload (JSON bytes by convention). Quota-limited to 512 B and 2/s per entity.</summary>
    [MemoryPackOrder(1)]
    public byte[] Json { get; set; } = [];

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public SetEntityPropsCommand()
    {
    }
}
