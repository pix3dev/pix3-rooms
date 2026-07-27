using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>S→C, TypeId <see cref="MessageTypeIds.PongEvent"/>. Reply to <see cref="PingCommand"/>.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class PongEvent
{
    /// <summary>Echo of <see cref="PingCommand.ClientTimeMs"/>.</summary>
    [MemoryPackOrder(0)]
    public long ClientTimeMs { get; set; }

    /// <summary>Server wall clock in Unix milliseconds when the ping was handled.</summary>
    [MemoryPackOrder(1)]
    public long ServerTimeMs { get; set; }

    /// <summary>Tick the ping was handled on.</summary>
    [MemoryPackOrder(2)]
    public uint ServerTick { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public PongEvent()
    {
    }
}
