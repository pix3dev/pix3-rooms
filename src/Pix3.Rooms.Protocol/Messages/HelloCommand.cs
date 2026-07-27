using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.HelloCommand"/>. Must be the <b>first</b> frame on a
/// connection; anything else earns <see cref="RejectCode.BadRequest"/> and close 4007.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class HelloCommand
{
    /// <summary>
    /// The <b>highest</b> version this client speaks. Negotiation is by range, not equality: below
    /// <see cref="ProtocolVersion.MinSupported"/> is rejected with 4001, anything else runs at
    /// <c>min(this, Current)</c> and is echoed in <see cref="WelcomeEvent.ProtocolVersion"/>.
    /// </summary>
    [MemoryPackOrder(0)]
    public ushort ProtocolVersion { get; set; }

    /// <summary>Room token (JWT, or a <c>dev:&lt;sub&gt;:&lt;roomId&gt;</c> string in insecure dev mode).</summary>
    [MemoryPackOrder(1)]
    public string Token { get; set; } = "";

    /// <summary>Room the client wants to join; must match the room bound into the token.</summary>
    [MemoryPackOrder(2)]
    public string RoomId { get; set; } = "";

    /// <summary>Requested display name. The server may sanitise, truncate or replace it.</summary>
    [MemoryPackOrder(3)]
    public string DisplayName { get; set; } = "";

    /// <summary>Client capability bits. Reserved; send 0.</summary>
    [MemoryPackOrder(4)]
    public ushort Capabilities { get; set; }

    /// <summary>
    /// The 16-byte key from a previous <see cref="WelcomeEvent.ResumeKey"/>, to re-attach a session
    /// that dropped inside its 30-second grace. Null (or any stale, wrong or expired value) is simply
    /// not a resume: it silently degrades to a fresh join, never an error path.
    /// </summary>
    [MemoryPackOrder(5)]
    public byte[]? ResumeKey { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public HelloCommand()
    {
    }
}
