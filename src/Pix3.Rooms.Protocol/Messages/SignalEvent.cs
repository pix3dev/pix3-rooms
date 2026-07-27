using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.RemoteEventBroadcast"/>. A relayed
/// <see cref="RemoteEventRequest"/> with the sender stamped by the server.
/// </summary>
[MemoryPackable]
public sealed partial class RemoteEventBroadcast
{
    /// <summary>Sender, resolved from the session — never copied from the request payload.</summary>
    public uint SenderClientId { get; set; }

    /// <summary>The event name as sent.</summary>
    public string Name { get; set; } = "";

    /// <summary>The payload as sent.</summary>
    public byte[] Payload { get; set; } = [];

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public RemoteEventBroadcast()
    {
    }
}
