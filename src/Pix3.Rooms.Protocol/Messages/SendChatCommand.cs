using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.SendChatCommand"/>. Quota-limited (10/min, 240 chars); the
/// sender's id is taken from the session, never from the payload.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class SendChatCommand
{
    /// <summary>Message text. Length-capped and sanitised by the server before fan-out.</summary>
    [MemoryPackOrder(0)]
    public string Text { get; set; } = "";

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public SendChatCommand()
    {
    }
}
