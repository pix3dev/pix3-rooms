using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.SetRoomVarCommand"/>. Writes one entry of the room's opaque
/// key/value bag. The server stores bytes and never interprets them.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class SetRoomVarCommand
{
    /// <summary>Variable name. Length-capped by the server.</summary>
    [MemoryPackOrder(0)]
    public string Key { get; set; } = "";

    /// <summary>Opaque value. Size-capped by the server; empty means "delete".</summary>
    [MemoryPackOrder(1)]
    public byte[] Value { get; set; } = [];

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public SetRoomVarCommand()
    {
    }
}
