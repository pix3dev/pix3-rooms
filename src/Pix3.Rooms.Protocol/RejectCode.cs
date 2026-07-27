namespace Pix3.Rooms.Protocol;

/// <summary>
/// Why a request, a join or a session was refused. Travels as <see cref="RejectedEvent.Code"/>
/// and as <see cref="SpawnEntityResponse.RejectCode"/>. Every close whose reason is known is
/// preceded by a <see cref="RejectedEvent"/> so the client can show a real message.
/// </summary>
public enum RejectCode : ushort
{
    /// <summary>No error. Never sent as a rejection; used as the "ok" value in responses.</summary>
    None = 0,

    /// <summary>
    /// <see cref="HelloCommand.ProtocolVersion"/> is below <see cref="ProtocolVersion.MinSupported"/>.
    /// Negotiation is by range, so a version <i>above</i> <see cref="ProtocolVersion.Current"/> is fine
    /// and never lands here. Close 4001.
    /// </summary>
    ProtocolVersionMismatch = 1,

    /// <summary>Room token missing, malformed or signature invalid. Close 4002.</summary>
    InvalidToken = 2,

    /// <summary>Room token is well-formed but past its expiry. Close 4002.</summary>
    TokenExpired = 3,

    /// <summary>Room token is valid but was minted for a different room. Close 4002.</summary>
    TokenRoomMismatch = 4,

    /// <summary>No room with the requested id exists on this server. Close 4003.</summary>
    RoomNotFound = 5,

    /// <summary>Room is at <c>MaxPlayers</c>. Close 4003.</summary>
    RoomFull = 6,

    /// <summary>Room is shutting down and refuses new members. Close 4003.</summary>
    RoomClosing = 7,

    /// <summary>Per-connection message/byte rate limit tripped. Close 4004.</summary>
    RateLimited = 8,

    /// <summary>A single frame exceeded <c>MaxPayloadBytes</c>. Close 4004.</summary>
    PayloadTooLarge = 9,

    /// <summary>A per-room or per-connection quota (spawns, chat, updates per frame) was exceeded. Close 4004.</summary>
    QuotaExceeded = 10,

    /// <summary>Process is draining. Close 4005.</summary>
    ServerShuttingDown = 11,

    /// <summary>Connection sent nothing for <c>IdleTimeoutSeconds</c>. Close 4006.</summary>
    IdleTimeout = 12,

    /// <summary>Frame was undecodable, out of order, or the first frame was not a Hello. Close 4007.</summary>
    BadRequest = 13,

    /// <summary>The same identity reconnected and displaced this session. Close 4008.</summary>
    SessionReplaced = 14,

    /// <summary>Room already holds <c>MaxEntities</c>. Spawn response only — never a close reason.</summary>
    EntityLimitReached = 15,

    /// <summary>
    /// Caller does not own the entity it tried to mutate or despawn. Spawn/despawn response only —
    /// never a close reason.
    /// </summary>
    NotEntityOwner = 16,

    /// <summary>Unexpected server-side failure. Close 4000.</summary>
    InternalError = 17,

    /// <summary>
    /// The requested <c>Kind</c> is not on the room's allowlist. An unknown kind would fault every
    /// observer's scene code, so it is refused at the spawn rather than replicated. Spawn response only
    /// — never a close reason.
    /// </summary>
    KindNotAllowed = 18,

    /// <summary>
    /// The connection's control send queue overflowed: this client could not drain what the room owed it,
    /// so the session is unrecoverably behind. Close 4004.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from <see cref="RateLimited"/>, which is the opposite fault — that one means
    /// the client sent too much, this one means it read too little. Collapsing them would make the
    /// telemetry lie about which side of the socket is in trouble, and would show the player a message
    /// blaming them for flooding when their connection is simply too slow.
    /// </remarks>
    SendQueueOverflow = 19,
}
