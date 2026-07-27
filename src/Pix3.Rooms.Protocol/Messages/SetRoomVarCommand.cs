using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.SetRoomVarRequest"/>. Writes one entry of the room's
/// opaque key/value bag. The server stores bytes and never interprets them.
/// </summary>
[MemoryPackable]
public sealed partial class SetRoomVarRequest
{
    /// <summary>Variable name. Length-capped by the server.</summary>
    public string Key { get; set; } = "";

    /// <summary>Opaque value. Size-capped by the server; empty means "delete".</summary>
    public byte[] Value { get; set; } = [];

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public SetRoomVarRequest()
    {
    }
}
