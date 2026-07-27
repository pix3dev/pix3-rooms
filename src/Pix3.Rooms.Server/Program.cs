using Microsoft.Extensions.Options;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server;
using Pix3.Rooms.Server.Admin;
using Pix3.Rooms.Server.Auth;
using Pix3.Rooms.Server.Net;
using Pix3.Rooms.Server.Observability;
using Pix3.Rooms.Server.Rooms;

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    builder.AddRoomsFabric();

    WebApplication app = builder.Build();

    // The pipeline itself lives in the composition root so a test can bring up the real one rather than a
    // hand-rolled copy — the transport-hardening assertions are worthless against a copy.
    app.UseRoomsFabric();

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
