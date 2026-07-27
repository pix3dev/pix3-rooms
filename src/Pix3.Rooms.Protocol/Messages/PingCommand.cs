using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>C→S, TypeId <see cref="MessageTypeIds.PingRequest"/>. Round-trip probe; also proof of liveness.</summary>
[MemoryPackable]
public sealed partial class PingRequest
{
    /// <summary>Client clock in milliseconds; echoed verbatim in <see cref="PongEvent"/>.</summary>
    public long ClientTimeMs { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public PingRequest()
    {
    }
}
