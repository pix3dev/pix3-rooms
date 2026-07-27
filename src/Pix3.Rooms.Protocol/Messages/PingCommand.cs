using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>C→S, TypeId <see cref="MessageTypeIds.PingCommand"/>. Round-trip probe; also proof of liveness.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class PingCommand
{
    /// <summary>Client clock in milliseconds; echoed verbatim in <see cref="PongEvent"/>.</summary>
    [MemoryPackOrder(0)]
    public long ClientTimeMs { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public PingCommand()
    {
    }
}
