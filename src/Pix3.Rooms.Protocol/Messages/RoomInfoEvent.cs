using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.RoomInfoEvent"/>. Coarse room telemetry, sent at roughly
/// 1 Hz. Cheap enough to broadcast unfiltered.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class RoomInfoEvent
{
    /// <summary>Current member count.</summary>
    [MemoryPackOrder(0)]
    public ushort PlayerCount { get; set; }

    /// <summary>Total live entities in the room (not the AOI-filtered count).</summary>
    [MemoryPackOrder(1)]
    public ushort EntityCount { get; set; }

    /// <summary>Tick the sample was taken on.</summary>
    [MemoryPackOrder(2)]
    public uint ServerTick { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public RoomInfoEvent()
    {
    }
}
