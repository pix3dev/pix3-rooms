using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.RoomVarsEvent"/>. The full room-var set on join, and only
/// the changed subset afterwards. <see cref="Keys"/> and <see cref="Values"/> are parallel arrays of
/// equal length.
/// </summary>
[MemoryPackable]
public sealed partial class RoomVarsEvent
{
    /// <summary>Variable names, positionally paired with <see cref="Values"/>.</summary>
    public string[] Keys { get; set; } = [];

    /// <summary>Opaque values, positionally paired with <see cref="Keys"/>.</summary>
    public byte[][] Values { get; set; } = [];

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public RoomVarsEvent()
    {
    }
}
