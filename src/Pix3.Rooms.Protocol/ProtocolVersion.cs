namespace Pix3.Rooms.Protocol;

/// <summary>
/// Wire-protocol version marker. Any change to a byte layout in <c>docs/protocol.md</c>
/// is a protocol version bump and must change <see cref="Current"/>.
/// </summary>
public static class ProtocolVersion
{
    /// <summary>
    /// The only version this build speaks. Sent in <see cref="HelloRequest"/> and echoed back in
    /// <see cref="WelcomeEvent"/>. A mismatch must produce <see cref="RejectCode.ProtocolVersionMismatch"/>
    /// (a typed rejection, never a decoder error).
    /// </summary>
    public const ushort Current = 1;
}
