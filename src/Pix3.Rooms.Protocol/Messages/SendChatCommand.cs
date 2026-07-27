using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.ChatMessageRequest"/>. Rate-limited by
/// <c>MaxChatPerMinute</c>; the sender's id is taken from the session, never from the payload.
/// </summary>
[MemoryPackable]
public sealed partial class ChatMessageRequest
{
    /// <summary>Message text. Length-capped and sanitised by the server before fan-out.</summary>
    public string Text { get; set; } = "";

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public ChatMessageRequest()
    {
    }
}
