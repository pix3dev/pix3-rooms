using Microsoft.Extensions.Options;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server;
using Pix3.Rooms.Server.Admin;
using Pix3.Rooms.Server.Auth;
using Pix3.Rooms.Server.Net;
using Pix3.Rooms.Server.Observability;
using Pix3.Rooms.Server.Rooms;

// Transport keepalive. The protocol has no server-to-client ping, so a silent-but-alive peer is detected
// by WebSocket control pings; the application idle timeout covers a peer that is alive but not playing.
// The timeout is what turns the ping into a detector rather than a formality: without it a dead mobile
// socket (radio gone, no FIN) stays "open" until TCP gives up, holding a connection slot and a room seat.
TimeSpan keepAliveInterval = TimeSpan.FromSeconds(15);
TimeSpan keepAliveTimeout = TimeSpan.FromSeconds(15);

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    builder.AddRoomsFabric();

    WebApplication app = builder.Build();

    app.UseWebSockets(new WebSocketOptions
    {
        KeepAliveInterval = keepAliveInterval,
        KeepAliveTimeout = keepAliveTimeout,
    });

    // Identity only: no capability, no configuration, nothing that helps an unauthenticated prober.
    app.MapGet("/", () => Results.Text($"pix3-rooms {ServerRuntimeInfo.Version}\n", "text/plain; charset=utf-8"));

    // Clients join with /ws?room=<id>. Mapped for every verb so a non-upgrade request reaches the
    // endpoint's own 400 instead of a framework 405.
    app.Map(RoomIdPolicy.WebSocketRoute, (HttpContext context, WebSocketEndpoint endpoint) => endpoint.HandleAsync(context));

    app.MapHealthEndpoint();
    app.MapMetricsEndpoint();
    app.MapRoomAdminApi();

    // Built eagerly, not on the first handshake: InsecureRoomTokenValidator logs its "tokens are NOT
    // verified" banner from its constructor, and JwtRoomTokenValidator rejects an unusable signing key
    // there too. Both belong in the startup log, not in the log line of the first client to connect.
    app.Services.GetRequiredService<IRoomTokenValidator>();

    // Every client gets a RejectEvent before its socket dies, so it can show a real message instead of
    // "connection lost". Room teardown follows through RoomManager's disposal — not duplicated here.
    ConnectionSupervisor supervisor = app.Services.GetRequiredService<ConnectionSupervisor>();
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        int closed = supervisor.CloseAll(RejectCode.ServerShuttingDown, "the server is shutting down");
        app.Logger.LogInformation("Shutting down: closed {ClosedConnections} live connection(s) with ServerShuttingDown.", closed);
    });

    AuthOptions authOptions = app.Services.GetRequiredService<AuthOptions>();
    NetOptions netOptions = app.Services.GetRequiredService<NetOptions>();
    MetricsOptions metricsOptions = app.Services.GetRequiredService<MetricsOptions>();
    ConfiguredOriginPolicy originPolicy = app.Services.GetRequiredService<ConfiguredOriginPolicy>();
    RoomServerOptions roomServerOptions = app.Services.GetRequiredService<IOptions<RoomServerOptions>>().Value;
    RoomDefaultsOptions roomDefaults = app.Services.GetRequiredService<IOptions<RoomDefaultsOptions>>().Value;

    // Normalize is idempotent and the IOptions value is the shared instance every room consumer uses, so
    // the summary reports the numbers that will actually be in force.
    roomServerOptions.Normalize();

    app.Logger.LogInformation(
        "pix3-rooms {Version} ready: env={Environment} auth={AuthMode} origins={AllowedOrigins} "
        + "ws={WebSocketPath} metrics={MetricsPath} metricsRequiresServiceToken={MetricsRequiresServiceToken} "
        + "maxRooms={MaxRooms} maxTotalConnections={MaxTotalConnections} "
        + "maxConcurrentUpgraded={MaxConcurrentUpgradedConnections} defaultTickHz={DefaultTickHz}",
        ServerRuntimeInfo.Version,
        app.Environment.EnvironmentName,
        authOptions.Mode,
        originPolicy.AllowsAnyOrigin ? "any (development only)" : string.Join(',', originPolicy.AllowedOrigins),
        RoomIdPolicy.WebSocketRoute,
        metricsOptions.Path,
        metricsOptions.RequireServiceToken,
        roomServerOptions.MaxRooms,
        netOptions.MaxTotalConnections,
        netOptions.MaxConcurrentUpgradedConnections,
        roomDefaults.TickHz);

    app.Run();
    return 0;
}
catch (Exception exception)
{
    // A startup failure is almost always a bad appsettings. Say what broke on stderr before the stack
    // trace, so the operator sees the reason instead of having to read a wall of frames.
    Console.Error.WriteLine($"FATAL: pix3-rooms could not start: {exception.Message}");
    Console.Error.WriteLine(exception);
    return 1;
}
