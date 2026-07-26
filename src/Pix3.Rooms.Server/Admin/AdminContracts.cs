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
public sealed record CreateRoomRequest(
    string? RoomId = null,
    string? ProjectId = null,
    string? BuildId = null,
    int? MaxPlayers = null,
    int? TickHz = null,
    float? AoiRadius = null,
    int? IdleTtlSeconds = null,
    int? MaxEntities = null,
    string? Mode = null);

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
/// <param name="BytesOutPerSecond">Recent outbound throughput.</param>
/// <param name="DroppedFrames">Frames dropped because a queue was full.</param>
/// <param name="BudgetOverruns">Ticks that exceeded their budget.</param>
public sealed record RoomStatsResponse(
    int PlayerCount,
    int EntityCount,
    uint ServerTick,
    double TickMsP50,
    double TickMsP99,
    long BytesOutPerSecond,
    long DroppedFrames,
    long BudgetOverruns);

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
