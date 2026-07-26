using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>S→C, TypeId <see cref="MessageTypeIds.PongEvent"/>. Reply to <see cref="PingRequest"/>.</summary>
[MemoryPackable]
public sealed partial class PongEvent
{
    /// <summary>Echo of <see cref="PingRequest.ClientTimeMs"/>.</summary>
    public long ClientTimeMs { get; set; }

    /// <summary>Server wall clock in Unix milliseconds when the ping was handled.</summary>
    public long ServerTimeMs { get; set; }

    /// <summary>Tick the ping was handled on.</summary>
    public uint ServerTick { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public PongEvent()
    {
    }
}
