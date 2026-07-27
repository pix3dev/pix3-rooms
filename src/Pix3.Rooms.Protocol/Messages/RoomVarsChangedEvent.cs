using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.RoomVarsChangedEvent"/>. The full room-var set on join, and
/// only the changed subset afterwards. <see cref="Keys"/> and <see cref="Values"/> are parallel arrays
/// of equal length.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class RoomVarsChangedEvent
{
    /// <summary>Variable names, positionally paired with <see cref="Values"/>.</summary>
    [MemoryPackOrder(0)]
    public string[] Keys { get; set; } = [];

    /// <summary>Opaque values, positionally paired with <see cref="Keys"/>.</summary>
    [MemoryPackOrder(1)]
    public byte[][] Values { get; set; } = [];

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public RoomVarsChangedEvent()
    {
    }
}
