using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.DespawnEntityCommand"/>. Only the owner may despawn an
/// entity; anyone else is refused with <see cref="RejectCode.NotEntityOwner"/>.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class DespawnEntityCommand
{
    /// <summary>The entity to remove. Its generation bits must still match the live slot.</summary>
    [MemoryPackOrder(0)]
    public uint NetId { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public DespawnEntityCommand()
    {
    }
}
