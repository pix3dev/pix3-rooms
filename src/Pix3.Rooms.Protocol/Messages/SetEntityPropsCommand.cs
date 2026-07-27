using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.SetEntityColdPropsRequest"/>. Replaces the entity's
/// low-frequency opaque blob. Owner-only. Sets <see cref="DeltaMask.ColdDirty"/> for subscribers,
/// which then receive an <see cref="EntityColdPropsEvent"/>.
/// </summary>
[MemoryPackable]
public sealed partial class SetEntityColdPropsRequest
{
    /// <summary>Target entity.</summary>
    public uint NetId { get; set; }

    /// <summary>Opaque payload (JSON bytes by convention). Size-capped by the server.</summary>
    public byte[] Json { get; set; } = [];

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public SetEntityColdPropsRequest()
    {
    }
}
