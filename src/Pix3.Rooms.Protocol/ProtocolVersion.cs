namespace Pix3.Rooms.Protocol;

/// <summary>
/// Wire-protocol version markers. Any change to a byte layout in <c>docs/protocol.md</c> is a protocol
/// version bump and must change <see cref="Current"/>.
/// </summary>
/// <remarks>
/// Negotiation is by <b>range, not equality</b>: a client announces the highest version it speaks, the
/// session runs at <c>min(client, Current)</c>, and only a client below <see cref="MinSupported"/> is
/// rejected — with <see cref="RejectCode.ProtocolVersionMismatch"/>, a typed rejection, never a decoder
/// error. Strict matching is right for a shipped game client and wrong for a platform that hosts other
/// people's bundles: it is what lets a game published six months ago keep working when the fabric grows.
/// </remarks>
public static class ProtocolVersion
{
    /// <summary>
    /// The highest version this build speaks. Announced in the handshake command and echoed back in the
    /// welcome event as the negotiated session version.
    /// </summary>
    public const ushort Current = 2;

    /// <summary>
    /// The lowest version this build still accepts. v2 is the first version that ever shipped, so v1
    /// support is deleted outright rather than maintained.
    /// </summary>
    public const ushort MinSupported = 2;

    /// <summary>
    /// True when a client-announced version can be served. An announcement above <see cref="Current"/> is
    /// fine — the session simply runs at <see cref="Current"/>.
    /// </summary>
    /// <param name="clientVersion">The highest version the client announced.</param>
    public static bool IsSupported(ushort clientVersion) => clientVersion >= MinSupported;

    /// <summary>
    /// Resolves the version a session with this client runs at: <c>min(clientVersion, Current)</c>.
    /// Call <see cref="IsSupported"/> first — this method does not judge, it only clamps downwards.
    /// </summary>
    /// <param name="clientVersion">The highest version the client announced.</param>
    public static ushort Negotiate(ushort clientVersion)
        => clientVersion < Current ? clientVersion : Current;
}
