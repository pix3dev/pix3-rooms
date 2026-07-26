using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server.Admin;

/// <summary>
/// The service-token-authenticated room lifecycle API. Mapped by the composition root as
/// <c>app.MapRoomAdminApi()</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><c>POST /admin/rooms</c> — create; 201 with the descriptor, 200 when the room
/// already exists with an identical effective configuration (idempotent retry), 409 when it exists with a
/// different one, 400 on validation failure, 503 when the server room cap is reached.</description></item>
/// <item><description><c>GET /admin/rooms</c> — every room with its live counters.</description></item>
/// <item><description><c>GET /admin/rooms/{roomId}</c> — one room; 404 when unknown.</description></item>
/// <item><description><c>DELETE /admin/rooms/{roomId}</c> — destroy; 204, or 404 when unknown.</description></item>
/// </list>
/// Every route sits behind <see cref="ServiceTokenEndpointFilter"/>. <c>GET /health</c> is mapped
/// separately by <see cref="HealthEndpoints"/> because liveness must stay unauthenticated.
/// </remarks>
public static class RoomAdminEndpoints
{
    /// <summary>Prefix the admin group is mounted on.</summary>
    public const string GroupPrefix = "/admin";

    /// <summary>Route of the room collection, relative to <see cref="GroupPrefix"/>.</summary>
    public const string RoomsRoute = "/rooms";

    private const string LogCategory = "Pix3.Rooms.Server.Admin.RoomAdminApi";
    private const string DefaultDestroyReason = "destroyed via admin API";

    /// <summary>
    /// Maps the admin API and returns its group so the composition root can add conventions (host
    /// filters, rate limiting) on top.
    /// </summary>
    /// <param name="app">Route builder to map onto.</param>
    public static RouteGroupBuilder MapRoomAdminApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup(GroupPrefix);
        group.AddEndpointFilter<ServiceTokenEndpointFilter>();

        group.MapPost(RoomsRoute, CreateRoom).WithName("CreateRoom");
        group.MapGet(RoomsRoute, ListRooms).WithName("ListRooms");
        group.MapGet($"{RoomsRoute}/{{roomId}}", GetRoom).WithName("GetRoom");
        group.MapDelete($"{RoomsRoute}/{{roomId}}", DeleteRoom).WithName("DeleteRoom");

        return group;
    }

    private static IResult CreateRoom(
        CreateRoomRequest? request,
        IRoomManager manager,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        RoomCreationDefaults defaults = RoomCreationDefaults.FromConfiguration(configuration);

        if (!RoomCreateValidator.TryBuildConfig(request, defaults, out RoomConfig? config, out IReadOnlyList<AdminFieldError> errors))
        {
            return TypedResults.BadRequest(new AdminErrorResponse(
                "invalid_request",
                "One or more fields were rejected.",
                errors));
        }

        // Idempotent retry: same id, same effective config → hand back the live room instead of a 409.
        if (manager.TryGet(config.RoomId, out IRoom? existing))
        {
            return DescribeExisting(existing, config);
        }

        if (defaults.MaxRooms is int cap && manager.RoomCount >= cap)
        {
            return Capacity($"Server room cap of {cap} reached.");
        }

        if (!manager.TryCreate(config, out IRoom? created, out RejectCode reject, out string? error))
        {
            // Lost a race against another creator: fall back to the idempotency answer.
            if (manager.TryGet(config.RoomId, out IRoom? raced))
            {
                return DescribeExisting(raced, config);
            }

            ILogger failureLogger = loggerFactory.CreateLogger(LogCategory);
            failureLogger.LogWarning(
                "Room creation refused for {RoomId} (project {ProjectId}): {Reject} {Error}",
                config.RoomId,
                config.ProjectId,
                reject,
                error ?? "no detail");

            return MapCreateFailure(reject, error);
        }

        ILogger logger = loggerFactory.CreateLogger(LogCategory);
        logger.LogInformation(
            "Room {RoomId} created for project {ProjectId} (build {BuildId}): mode {Mode}, {MaxPlayers} players, {TickHz} Hz, AOI {AoiRadius}, {MaxEntities} entities.",
            config.RoomId,
            config.ProjectId,
            config.BuildId.Length == 0 ? "-" : config.BuildId,
            config.Mode,
            config.MaxPlayers,
            config.TickHz,
            config.AoiRadius,
            config.MaxEntities);

        return TypedResults.Created(RoomLocation(config.RoomId), Describe(created));
    }

    private static IResult ListRooms(IRoomManager manager)
    {
        IReadOnlyList<RoomConfig> configs = manager.ListConfigs();
        List<RoomResponse> rooms = new(configs.Count);

        // RoomStats carries no room id, so join through the registry instead of trusting list ordering.
        for (int i = 0; i < configs.Count; i++)
        {
            if (manager.TryGet(configs[i].RoomId, out IRoom? room))
            {
                rooms.Add(Describe(room));
            }
        }

        return TypedResults.Ok(new RoomListResponse(rooms.Count, rooms));
    }

    private static IResult GetRoom(string roomId, IRoomManager manager)
    {
        if (!RoomIdPolicy.IsValid(roomId))
        {
            return InvalidRoomId();
        }

        if (!manager.TryGet(roomId, out IRoom? room))
        {
            return NotFound(roomId);
        }

        return TypedResults.Ok(Describe(room));
    }

    private static IResult DeleteRoom(
        string roomId,
        string? reason,
        IRoomManager manager,
        ILoggerFactory loggerFactory)
    {
        if (!RoomIdPolicy.IsValid(roomId))
        {
            return InvalidRoomId();
        }

        string sanitized = SanitizeReason(reason);
        if (!manager.Destroy(roomId, sanitized))
        {
            return NotFound(roomId);
        }

        ILogger logger = loggerFactory.CreateLogger(LogCategory);
        logger.LogInformation("Room {RoomId} destroyed via admin API: {Reason}", roomId, sanitized);

        return TypedResults.NoContent();
    }

    private static IResult DescribeExisting(IRoom existing, RoomConfig requested)
    {
        if (existing.Config == requested)
        {
            return TypedResults.Ok(Describe(existing));
        }

        return TypedResults.Conflict(new AdminErrorResponse(
            "room_exists",
            $"Room '{requested.RoomId}' already exists with a different configuration."));
    }

    private static RoomResponse Describe(IRoom room)
    {
        RoomConfig config = room.Config;
        RoomStats stats = room.SnapshotStats();

        RoomDescriptor descriptor = new(
            config.RoomId,
            config.ProjectId,
            config.BuildId,
            config.MaxPlayers,
            config.TickHz,
            config.AoiRadius,
            config.IdleTtlSeconds,
            config.MaxEntities,
            config.Mode.ToString(),
            room.PlayerCount,
            room.CreatedAt,
            room.LastActivityAt,
            RoomIdPolicy.WebSocketPath(config.RoomId));

        RoomStatsResponse statsResponse = new(
            stats.PlayerCount,
            stats.EntityCount,
            stats.ServerTick,
            stats.TickMsP50,
            stats.TickMsP99,
            stats.BytesOutPerSecond,
            stats.DroppedFrames,
            stats.BudgetOverruns);

        return new RoomResponse(descriptor, statsResponse);
    }

    private static IResult MapCreateFailure(RejectCode reject, string? error)
    {
        string message = string.IsNullOrWhiteSpace(error) ? $"Room creation refused ({reject})." : error;

        return reject switch
        {
            RejectCode.RoomFull or RejectCode.QuotaExceeded or RejectCode.RateLimited => Capacity(message),
            RejectCode.RoomClosing or RejectCode.ServerShuttingDown => Unavailable("server_draining", message),
            RejectCode.BadRequest => TypedResults.BadRequest(new AdminErrorResponse("invalid_request", message)),
            _ => TypedResults.Json(
                new AdminErrorResponse("internal_error", message),
                statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static IResult Capacity(string message) => Unavailable("capacity_reached", message);

    private static IResult Unavailable(string code, string message)
        => TypedResults.Json(
            new AdminErrorResponse(code, message),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult InvalidRoomId()
        => TypedResults.BadRequest(new AdminErrorResponse(
            "invalid_room_id",
            $"Room ids must match [A-Za-z0-9_-]{{1,{RoomIdPolicy.MaxLength}}}.",
            new AdminFieldError[] { new("roomId", "Malformed room id.") }));

    private static IResult NotFound(string roomId)
        => TypedResults.NotFound(new AdminErrorResponse(
            "room_not_found",
            $"No room '{roomId}' on this server."));

    private static string RoomLocation(string roomId)
        => $"{GroupPrefix}{RoomsRoute}/{Uri.EscapeDataString(roomId)}";

    /// <summary>
    /// Bounds and de-fangs an operator-supplied destroy reason: it travels into log lines and into room
    /// shutdown paths, so control characters and unbounded length are not welcome.
    /// </summary>
    private static string SanitizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return DefaultDestroyReason;
        }

        string trimmed = reason.Trim();
        if (trimmed.Length > RoomLimits.ReasonMaxLength)
        {
            trimmed = trimmed[..RoomLimits.ReasonMaxLength];
        }

        Span<char> buffer = trimmed.Length <= 256 ? stackalloc char[trimmed.Length] : new char[trimmed.Length];
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            buffer[i] = char.IsControl(c) ? ' ' : c;
        }

        return new string(buffer);
    }
}
