namespace Pix3.Rooms.Server.Admin;

/// <summary>
/// Body of <c>POST /admin/rooms</c>. Every field except <see cref="ProjectId"/> is optional; omitted
/// values come from the <c>Rooms:Defaults</c> configuration section.
/// </summary>
/// <param name="RoomId">Desired room id (<c>[A-Za-z0-9_-]{1,64}</c>). The server generates one when absent.</param>
/// <param name="ProjectId">Owning pix3 project. Required.</param>
/// <param name="BuildId">Optional build/version tag of the game creating the room.</param>
/// <param name="MaxPlayers">Hard member cap.</param>
/// <param name="TickHz">Room tick rate.</param>
/// <param name="AoiRadius">Area-of-interest radius in world units.</param>
/// <param name="IdleTtlSeconds">Seconds the room may stay empty before the sweeper destroys it.</param>
/// <param name="MaxEntities">Entity-table capacity.</param>
/// <param name="Mode">Authority model: <c>Relay</c> or <c>Authoritative</c> (case-insensitive).</param>
/// <param name="MaxVisibleEntities">
/// k-nearest visibility cap per client. The one AOI cap a game legitimately tunes per room, and the value
/// <c>WelcomeEvent</c> carries.
/// </param>
/// <param name="WorldOriginX">World-space X of the low corner of the room's quantization range.</param>
/// <param name="WorldOriginY">World-space Y of the low corner of the room's quantization range.</param>
/// <param name="WorldSize">
/// Side length of the square world every quantized value in the room is expressed against. Required when
/// either origin is supplied: half a world is not a world.
/// </param>
/// <param name="AllowedKinds">
/// Entity kinds this room accepts, as indexes into the build's prefab table. Omitted or empty inherits
/// <c>Rooms:Defaults:AllowedKinds</c>; a room that ends up with an empty list accepts any kind, which the
/// composition root refuses to start with in Production.
/// </param>
public sealed record CreateRoomRequest(
    string? RoomId = null,
    string? ProjectId = null,
    string? BuildId = null,
    int? MaxPlayers = null,
    int? TickHz = null,
    float? AoiRadius = null,
    int? IdleTtlSeconds = null,
    int? MaxEntities = null,
    string? Mode = null,
    int? MaxVisibleEntities = null,
    float? WorldOriginX = null,
    float? WorldOriginY = null,
    float? WorldSize = null,
    IReadOnlyList<int>? AllowedKinds = null);

/// <summary>A room's effective configuration plus the identity clients need to connect.</summary>
/// <param name="RoomId">Room id.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="BuildId">Build tag, empty when unset.</param>
/// <param name="MaxPlayers">Hard member cap.</param>
/// <param name="TickHz">Room tick rate.</param>
/// <param name="AoiRadius">Area-of-interest radius.</param>
/// <param name="IdleTtlSeconds">Empty-room TTL.</param>
/// <param name="MaxEntities">Entity-table capacity.</param>
/// <param name="Mode">Authority model name.</param>
/// <param name="MaxVisibleEntities">k-nearest visibility cap per client.</param>
/// <param name="WorldOriginX">World-space X of the low corner of the quantization range.</param>
/// <param name="WorldOriginY">World-space Y of the low corner of the quantization range.</param>
/// <param name="WorldSize">Side length of the square world quantized values are expressed against.</param>
/// <param name="AllowedKinds">Accepted entity kinds; empty means any kind is accepted.</param>
/// <param name="PlayerCount">Members currently joined.</param>
/// <param name="CreatedAt">When the room was created.</param>
/// <param name="LastActivityAt">Last join, leave or inbound message.</param>
/// <param name="WebSocketPath">Path (with query) clients should open, e.g. <c>/ws?room=abc</c>.</param>
public sealed record RoomDescriptor(
    string RoomId,
    string ProjectId,
    string BuildId,
    int MaxPlayers,
    int TickHz,
    float AoiRadius,
    int IdleTtlSeconds,
    int MaxEntities,
    string Mode,
    int MaxVisibleEntities,
    float WorldOriginX,
    float WorldOriginY,
    float WorldSize,
    IReadOnlyList<ushort> AllowedKinds,
    int PlayerCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    string WebSocketPath);

/// <summary>Point-in-time room counters, mirroring <c>RoomStats</c>.</summary>
/// <param name="PlayerCount">Members currently joined.</param>
/// <param name="EntityCount">Live entities.</param>
/// <param name="ServerTick">Most recently completed tick.</param>
/// <param name="TickMsP50">Median tick duration, milliseconds.</param>
/// <param name="TickMsP99">99th-percentile tick duration, milliseconds.</param>
/// <param name="TickJitterMsP99">
/// 99th-percentile lateness of a tick's start against its absolute deadline — the number that proves the
/// scheduler is working, which tick body time cannot.
/// </param>
/// <param name="BytesOutPerSecond">Recent outbound throughput.</param>
/// <param name="DroppedFrames">Frames dropped because a queue was full.</param>
/// <param name="BudgetOverruns">Ticks that exceeded their budget.</param>
/// <param name="Resyncs">Known-set rebuilds (hot-lane overflow, <c>ResyncCommand</c>).</param>
/// <param name="Violations">Sum of every member's violation counters at the last publish.</param>
public sealed record RoomStatsResponse(
    int PlayerCount,
    int EntityCount,
    uint ServerTick,
    double TickMsP50,
    double TickMsP99,
    double TickJitterMsP99,
    long BytesOutPerSecond,
    long DroppedFrames,
    long BudgetOverruns,
    long Resyncs,
    long Violations);

/// <summary>
/// One client's violation tallies, as <c>GET /admin/rooms/{roomId}/violations/{clientId}</c> returns them.
/// </summary>
/// <remarks>
/// The dataset the anti-cheat detector will eventually run on. Every field is a lifetime count for this
/// session; a client the room does not know reports zeros rather than 404, because "no violations" and
/// "never seen" are the same answer to the only question this endpoint exists to answer.
/// </remarks>
/// <param name="RoomId">Room the client belongs to.</param>
/// <param name="ClientId">Client the tallies are for.</param>
/// <param name="Ownership">Entity mutations aimed at an entity the sender does not own.</param>
/// <param name="Speed">Moves that failed the counted-only Level-1 speed check.</param>
/// <param name="Mask">Illegal delta masks and records the decoder refused.</param>
/// <param name="Nan">Non-finite floats (spectator focus is the only inbound float left).</param>
/// <param name="Kind">Spawns naming an entity kind outside the room's allowlist.</param>
/// <param name="Quota">Connection- and room-level quota refusals attributed to this client.</param>
/// <param name="FocusClamp">Spectator focus moves that hit the per-tick speed clamp.</param>
/// <param name="Teleport">Client-set teleport bits (legitimate under client authority, so counted).</param>
public sealed record RoomViolationsResponse(
    string RoomId,
    uint ClientId,
    long Ownership,
    long Speed,
    long Mask,
    long Nan,
    long Kind,
    long Quota,
    long FocusClamp,
    long Teleport);

/// <summary>One room as the admin API returns it.</summary>
/// <param name="Room">Configuration and connection info.</param>
/// <param name="Stats">Live counters.</param>
public sealed record RoomResponse(RoomDescriptor Room, RoomStatsResponse Stats);

/// <summary>Result of <c>GET /admin/rooms</c>.</summary>
/// <param name="Count">Number of rooms returned.</param>
/// <param name="Rooms">The rooms.</param>
public sealed record RoomListResponse(int Count, IReadOnlyList<RoomResponse> Rooms);

/// <summary>One field-level validation problem.</summary>
/// <param name="Field">Request field the problem is about.</param>
/// <param name="Message">What is wrong with it.</param>
public sealed record AdminFieldError(string Field, string Message);

/// <summary>Error body for every non-2xx admin response except 401, which carries no body at all.</summary>
/// <param name="Error">Stable machine-readable code, e.g. <c>invalid_request</c>.</param>
/// <param name="Message">Human-readable summary.</param>
/// <param name="Fields">Per-field problems; empty when the error is not field-specific.</param>
public sealed record AdminErrorResponse(string Error, string Message, IReadOnlyList<AdminFieldError> Fields)
{
    /// <summary>Creates an error with no field-level detail.</summary>
    public AdminErrorResponse(string error, string message)
        : this(error, message, Array.Empty<AdminFieldError>())
    {
    }
}

/// <summary>Liveness payload of <c>GET /health</c>.</summary>
/// <param name="Status">Always <c>ok</c> while the process serves requests.</param>
/// <param name="UptimeSeconds">Seconds since process start.</param>
/// <param name="Version">Informational assembly version.</param>
/// <param name="Rooms">Rooms currently alive.</param>
/// <param name="Connections">WebSocket connections currently open.</param>
public sealed record HealthResponse(
    string Status,
    double UptimeSeconds,
    string Version,
    int Rooms,
    int Connections);
