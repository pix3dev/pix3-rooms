using System.Net.WebSockets;

namespace Pix3.Rooms.Protocol;

/// <summary>Maps <see cref="RejectCode"/> onto the WebSocket close codes fixed by the wire spec.</summary>
public static class RejectCodeExtensions
{
    /// <summary>Close code used when a reject code has no spec-defined mapping.</summary>
    public const int FallbackCloseCode = 4000;

    /// <summary>
    /// The numeric WebSocket close code for this reject code, per the spec table.
    /// The 4000-range values are application close codes and are therefore returned as a cast
    /// <see cref="WebSocketCloseStatus"/> (the enum has no members for them).
    /// </summary>
    /// <remarks>
    /// <see cref="RejectCode.None"/> maps to <see cref="WebSocketCloseStatus.NormalClosure"/> (1000).
    /// <see cref="RejectCode.EntityLimitReached"/> and <see cref="RejectCode.NotEntityOwner"/> are
    /// ack-only codes with no close mapping; they fall back to 4007 (bad request) so a caller that
    /// closes on them anyway still emits a defined code. Use <see cref="HasWebSocketCloseStatus"/>
    /// to tell a real mapping from a fallback.
    /// </remarks>
    public static WebSocketCloseStatus ToWebSocketCloseStatus(this RejectCode code) => code switch
    {
        RejectCode.None => WebSocketCloseStatus.NormalClosure,
        RejectCode.ProtocolVersionMismatch => (WebSocketCloseStatus)4001,
        RejectCode.InvalidToken => (WebSocketCloseStatus)4002,
        RejectCode.TokenExpired => (WebSocketCloseStatus)4002,
        RejectCode.TokenRoomMismatch => (WebSocketCloseStatus)4002,
        RejectCode.RoomNotFound => (WebSocketCloseStatus)4003,
        RejectCode.RoomFull => (WebSocketCloseStatus)4003,
        RejectCode.RoomClosing => (WebSocketCloseStatus)4003,
        RejectCode.RateLimited => (WebSocketCloseStatus)4004,
        RejectCode.PayloadTooLarge => (WebSocketCloseStatus)4004,
        RejectCode.QuotaExceeded => (WebSocketCloseStatus)4004,
        RejectCode.ServerShuttingDown => (WebSocketCloseStatus)4005,
        RejectCode.IdleTimeout => (WebSocketCloseStatus)4006,
        RejectCode.BadRequest => (WebSocketCloseStatus)4007,
        RejectCode.SessionReplaced => (WebSocketCloseStatus)4008,
        RejectCode.EntityLimitReached => (WebSocketCloseStatus)4007,
        RejectCode.NotEntityOwner => (WebSocketCloseStatus)4007,
        RejectCode.InternalError => (WebSocketCloseStatus)FallbackCloseCode,
        _ => (WebSocketCloseStatus)FallbackCloseCode,
    };

    /// <summary>
    /// True when the spec defines a close code for this reject code, i.e. it is a legitimate reason
    /// to terminate a session. False for <see cref="RejectCode.None"/> and the ack-only codes.
    /// </summary>
    public static bool HasWebSocketCloseStatus(this RejectCode code) => code switch
    {
        RejectCode.None => false,
        RejectCode.EntityLimitReached => false,
        RejectCode.NotEntityOwner => false,
        _ => true,
    };

    /// <summary>The close code as a plain <see cref="int"/>, for logging and metrics.</summary>
    public static int ToCloseCode(this RejectCode code) => (int)code.ToWebSocketCloseStatus();
}
