namespace Pix3.Rooms.Server.Observability;

/// <summary>
/// Why an outbound or inbound frame was thrown away. Label values for
/// <c>frames_dropped_total{reason}</c>.
/// </summary>
/// <remarks>
/// Contiguous from zero and closed with <see cref="Other"/> so the metric facade can index it with an
/// array and keep cardinality fixed.
/// </remarks>
public enum FrameDropReason : byte
{
    /// <summary>The connection's bounded send channel was full.</summary>
    OutboundQueueFull = 0,

    /// <summary>The room's bounded inbound channel was full.</summary>
    InboundQueueFull = 1,

    /// <summary>The socket was already closing when the frame was handed over.</summary>
    ConnectionClosed = 2,

    /// <summary>The frame would have exceeded the configured payload cap.</summary>
    FrameTooLarge = 3,

    /// <summary>Encoding the frame failed.</summary>
    EncodeFailed = 4,

    /// <summary>The socket write itself failed.</summary>
    SendFailed = 5,

    /// <summary>Anything else, and the collapse target for unmapped values.</summary>
    Other = 6,
}

/// <summary>Which quota a client tripped. Label values for <c>quota_violations_total{kind}</c>.</summary>
public enum QuotaKind : byte
{
    /// <summary>Messages per second per connection.</summary>
    MessageRate = 0,

    /// <summary>Bytes per second per connection.</summary>
    ByteRate = 1,

    /// <summary>Single-frame payload size cap.</summary>
    PayloadSize = 2,

    /// <summary>Entity delta records in one <c>EntityUpdateFrame</c>.</summary>
    EntityUpdatesPerFrame = 3,

    /// <summary>Spawn requests per minute.</summary>
    SpawnRate = 4,

    /// <summary>Chat messages per minute.</summary>
    ChatRate = 5,

    /// <summary>Room entity-table capacity.</summary>
    EntityLimit = 6,

    /// <summary>An update or despawn targeting an entity the sender does not own.</summary>
    NotEntityOwner = 7,

    /// <summary>Room-variable count or value size.</summary>
    RoomVarSize = 8,

    /// <summary>Anything else, and the collapse target for unmapped values.</summary>
    Other = 9,
}

/// <summary>Why authentication failed. Label values for <c>auth_failures_total{reason}</c>.</summary>
public enum AuthFailureReason : byte
{
    /// <summary>No token was presented at all.</summary>
    MissingToken = 0,

    /// <summary>The token was not parseable.</summary>
    MalformedToken = 1,

    /// <summary>Signature validation failed.</summary>
    InvalidSignature = 2,

    /// <summary>The token was well-formed but expired.</summary>
    Expired = 3,

    /// <summary>The token was valid but minted for another room.</summary>
    RoomMismatch = 4,

    /// <summary>An admin or metrics request presented a bad service token.</summary>
    ServiceTokenInvalid = 5,

    /// <summary>Anything else, and the collapse target for unmapped values.</summary>
    Other = 6,
}
