using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.EntitySpawnAckEvent"/>. The answer to one
/// <see cref="EntitySpawnRequest"/>, sent only to the requester.
/// </summary>
[MemoryPackable]
public sealed partial class EntitySpawnAckEvent
{
    /// <summary>Echo of <see cref="EntitySpawnRequest.RequestId"/>.</summary>
    public uint RequestId { get; set; }

    /// <summary>The assigned net id, or 0 when the spawn was refused.</summary>
    public uint NetId { get; set; }

    /// <summary>A <c>RejectCode</c> value; 0 means the spawn succeeded.</summary>
    public ushort RejectCode { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public EntitySpawnAckEvent()
    {
    }
}
