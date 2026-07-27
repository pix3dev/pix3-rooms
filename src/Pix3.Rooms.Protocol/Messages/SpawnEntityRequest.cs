using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.EntitySpawnRequest"/>. Asks the room to create an entity
/// owned by the sender. Answered by exactly one <see cref="EntitySpawnAckEvent"/>.
/// </summary>
[MemoryPackable]
public sealed partial class EntitySpawnRequest
{
    /// <summary>Client-chosen correlation id, echoed in the ack. Not a net id.</summary>
    public uint RequestId { get; set; }

    /// <summary>Application-defined entity kind. Opaque to the server.</summary>
    public ushort Kind { get; set; }

    /// <summary>Initial world X.</summary>
    public float X { get; set; }

    /// <summary>Initial world Y.</summary>
    public float Y { get; set; }

    /// <summary>Initial rotation in radians.</summary>
    public float Rot { get; set; }

    /// <summary>Optional opaque cold props (JSON bytes by convention). Size-capped by the server.</summary>
    public byte[]? ColdProps { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public EntitySpawnRequest()
    {
    }
}
