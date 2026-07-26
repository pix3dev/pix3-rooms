using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.RoomInfoEvent"/>. Coarse room telemetry, sent at roughly
/// 1 Hz. Cheap enough to broadcast unfiltered.
/// </summary>
[MemoryPackable]
public sealed partial class RoomInfoEvent
{
    /// <summary>Current member count.</summary>
    public ushort PlayerCount { get; set; }

    /// <summary>Total live entities in the room (not the AOI-filtered count).</summary>
    public ushort EntityCount { get; set; }

    /// <summary>Tick the sample was taken on.</summary>
    public uint ServerTick { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public RoomInfoEvent()
    {
    }
}
