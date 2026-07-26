using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.RemoteEventRequest"/>. A named application-level event the
/// server relays according to <see cref="Target"/>. The server never interprets
/// <see cref="Name"/> or <see cref="Payload"/>.
/// </summary>
[MemoryPackable]
public sealed partial class RemoteEventRequest
{
    /// <summary>Application-defined event name. Length-capped by the server.</summary>
    public string Name { get; set; } = "";

    /// <summary>A <c>RemoteEventTarget</c> value selecting the fan-out.</summary>
    public byte Target { get; set; }

    /// <summary>Recipient when <see cref="Target"/> is <c>SinglePeer</c>; ignored otherwise.</summary>
    public uint TargetClientId { get; set; }

    /// <summary>Opaque payload. Size-capped by the server.</summary>
    public byte[] Payload { get; set; } = [];

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public RemoteEventRequest()
    {
    }
}
