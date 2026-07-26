using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>S→C, TypeId <see cref="MessageTypeIds.ChatMessageEvent"/>. A chat line attributed to a member.</summary>
[MemoryPackable]
public sealed partial class ChatMessageEvent
{
    /// <summary>Sender, resolved by the server from the session that sent the request.</summary>
    public uint ClientId { get; set; }

    /// <summary>The accepted text.</summary>
    public string Text { get; set; } = "";

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public ChatMessageEvent()
    {
    }
}
