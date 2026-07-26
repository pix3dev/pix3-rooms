using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// A live socket. Room logic only ever sees this interface — it must never learn about Kestrel,
/// <c>WebSocket</c> or the send loop.
/// </summary>
/// <remarks>
/// Implementations are touched from the room thread (<see cref="TryEnqueue"/>,
/// <see cref="RequestClose"/>) and from the socket's own read/send loops, so every member here has to
/// be safe to call concurrently with the transport.
/// </remarks>
public interface IClientConnection
{
    /// <summary>Room-unique id, monotonic per server. Also the entity owner id.</summary>
    uint ClientId { get; }

    /// <summary>Remote address, used for per-IP connection caps and logging.</summary>
    string RemoteIp { get; }

    /// <summary>The display name the server accepted at handshake time.</summary>
    string DisplayName { get; }

    /// <summary>False once the socket is closing or closed; enqueueing then always fails.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Enqueue an already-encoded frame. False = queue full or connection closed, in which case the
    /// caller keeps ownership of <paramref name="frame"/> and must return its buffer to
    /// <see cref="FramePool"/>.
    /// </summary>
    /// <remarks>Non-blocking by contract: the room tick must never wait on a socket.</remarks>
    bool TryEnqueue(in OutboundFrame frame);

    /// <summary>
    /// Send a <see cref="RejectEvent"/> (when <paramref name="code"/> is not
    /// <see cref="RejectCode.None"/>) and close with the status from
    /// <see cref="RejectCodeExtensions.ToWebSocketCloseStatus"/>. Idempotent and non-blocking.
    /// </summary>
    /// <param name="code">Why the session is ending.</param>
    /// <param name="reason">Human-readable detail for the client; never secrets or stack traces.</param>
    void RequestClose(RejectCode code, string reason);
}
