using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>S→C, TypeId <see cref="MessageTypeIds.ChatMessageEvent"/>. A chat line attributed to a member.</summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class ChatMessageEvent
{
    /// <summary>Sender, resolved by the server from the session that sent the command.</summary>
    [MemoryPackOrder(0)]
    public uint ClientId { get; set; }

    /// <summary>The accepted text.</summary>
    [MemoryPackOrder(1)]
    public string Text { get; set; } = "";

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public ChatMessageEvent()
    {
    }
}
