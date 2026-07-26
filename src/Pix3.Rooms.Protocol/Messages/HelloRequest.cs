using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.HelloRequest"/>. Must be the <b>first</b> frame on a
/// connection; anything else earns <see cref="RejectCode.BadRequest"/> and close 4007.
/// </summary>
[MemoryPackable]
public sealed partial class HelloRequest
{
    /// <summary>Must equal <see cref="ProtocolVersion.Current"/> or the session is rejected with 4001.</summary>
    public ushort ProtocolVersion { get; set; }

    /// <summary>Room token (JWT, or a <c>dev:&lt;sub&gt;:&lt;roomId&gt;</c> string in insecure dev mode).</summary>
    public string Token { get; set; } = "";

    /// <summary>Room the client wants to join; must match the room bound into the token.</summary>
    public string RoomId { get; set; } = "";

    /// <summary>Requested display name. The server may sanitise, truncate or replace it.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Client capability bits. Reserved; send 0 in v1.</summary>
    public ushort Capabilities { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public HelloRequest()
    {
    }
}
