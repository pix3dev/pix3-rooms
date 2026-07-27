using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.SpawnEntityResponse"/>. The answer to one
/// <see cref="SpawnEntityRequest"/>, sent only to the requester.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class SpawnEntityResponse
{
    /// <summary>Echo of <see cref="SpawnEntityRequest.RequestId"/>.</summary>
    [MemoryPackOrder(0)]
    public uint RequestId { get; set; }

    /// <summary>The assigned net id, or 0 when the spawn was refused.</summary>
    [MemoryPackOrder(1)]
    public uint NetId { get; set; }

    /// <summary>A <see cref="Protocol.RejectCode"/> value; 0 means the spawn succeeded.</summary>
    [MemoryPackOrder(2)]
    public ushort RejectCode { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public SpawnEntityResponse()
    {
    }
}
