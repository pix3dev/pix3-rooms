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
    /// <summary>
    /// Room-unique, monotonic per server. Also the entity owner id.
    /// </summary>
    /// <remarks>
    /// Allocated by <c>Net</c> only <b>after</b> the handshake authenticates — an unauthenticated socket
    /// consumes no id and no room state, and reports <c>0</c> here — and replaced by the resumed session's
    /// original id when a resume succeeds (<c>Net</c> owns the allocator; the room hands the old id back in
    /// a <c>JoinGrant</c>, and the transport adopts it before the member is published).
    /// </remarks>
    uint ClientId { get; }

    /// <summary>Remote address, used for per-IP connection caps and logging.</summary>
    string RemoteIp { get; }

    /// <summary>The display name the server accepted at handshake time.</summary>
    string DisplayName { get; }

    /// <summary>False once the socket is closing or closed; enqueueing then always fails.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Enqueue an already-encoded frame on a lane. False = queue full or closed, in which case the caller
    /// still owns <paramref name="frame"/> and must return its buffer to <see cref="FramePool"/>.
    /// <b>Ownership transfers on success only.</b>
    /// </summary>
    /// <param name="frame">A complete <c>[TypeId][payload]</c> frame in a rented buffer.</param>
    /// <param name="lane">
    /// Which queue to use. The failure policies differ and that is the point:
    /// <see cref="FrameLane.Control"/> additionally <i>closes the connection</i> on a full queue, because a
    /// control frame that is silently dropped has no repair mechanism; <see cref="FrameLane.Hot"/> merely
    /// reports failure, and the caller must roll back that frame's known-set changes and mark the client
    /// for resync.
    /// </param>
    /// <remarks>Non-blocking by contract: the room tick must never wait on a socket.</remarks>
    bool TryEnqueue(in OutboundFrame frame, FrameLane lane);

    /// <summary>
    /// Send a <see cref="RejectedEvent"/> (when <paramref name="code"/> is not
    /// <see cref="RejectCode.None"/>) and close with the status from
    /// <see cref="RejectCodeExtensions.ToWebSocketCloseStatus"/>. Idempotent and non-blocking.
    /// </summary>
    /// <param name="code">Why the session is ending.</param>
    /// <param name="reason">Human-readable detail for the client; never secrets or stack traces.</param>
    void RequestClose(RejectCode code, string reason);
}
