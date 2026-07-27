using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pix3.Rooms.LoadGen;
using Pix3.Rooms.Server;

namespace Pix3.Rooms.Tests.EndToEnd;

/// <summary>
/// A real pix3-rooms server on a loopback port, composed by the production composition root.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RoomsFabricExtensions.AddRoomsFabric"/> and
/// <see cref="RoomsFabricExtensions.UseRoomsFabric"/> are the same two calls <c>Program</c> makes, so
/// what these tests exercise is the shipped wiring — Kestrel pinned to HTTP/1.1, the WebSocket options,
/// the pre-auth gate, the admin API, the metrics endpoint — and not a hand-rolled stand-in. A test
/// against a copy of the pipeline would prove nothing about transport hardening, which is precisely the
/// thing you can only observe on the wire.
/// </para>
/// <para>
/// Auth runs in <c>Insecure</c> mode (unsigned <c>dev:&lt;subject&gt;:&lt;roomId&gt;</c> tokens), which the
/// server permits in Development and refuses in Production. The per-IP caps are raised because every
/// client here comes from 127.0.0.1; the shipped defaults of 8 and 4 are a production policy, not a test
/// fixture, and leaving them in place would make the fixture measure the cap rather than the protocol.
/// </para>
/// </remarks>
public sealed class LiveServerFixture : IAsyncLifetime
{
    /// <summary>The token the admin API accepts, long enough to clear the "worth calling a secret" bar.</summary>
    public const string ServiceToken = "test-service-token-0123456789abcdef";

    private WebApplication? _app;

    /// <summary>The server's base address, known only after it has bound its port.</summary>
    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:0");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(LiveServerFixture).Assembly.GetName().Name,
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Rooms:Auth:Mode"] = "Insecure",
            ["Rooms:Auth:ServiceToken"] = ServiceToken,
            ["Rooms:Auth:AllowedOrigins:0"] = "",          // any origin: no browser is involved here
            ["Rooms:Quotas:MaxConnectionsPerIp"] = "512",
            ["Rooms:Server:MaxPreAuthConnectionsPerIp"] = "512",
            ["Rooms:Server:MaxTotalConnections"] = "1024",
            ["Rooms:Server:ResumeGraceSeconds"] = "30",
            ["Metrics:RequireServiceToken"] = "false",

            // Port 0: the OS picks a free one, so parallel test classes never collide.
            ["Urls"] = "http://127.0.0.1:0",
        });

        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.AddRoomsFabric();

        _app = builder.Build();
        _app.UseRoomsFabric();

        await _app.StartAsync();

        string? address = _app.Urls.FirstOrDefault()
            ?? throw new InvalidOperationException("the test server bound no address");
        BaseUri = new Uri(address);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>An admin client authenticated with <see cref="ServiceToken"/>.</summary>
    public AdminApiClient CreateAdminClient() => new(BaseUri, ServiceToken);

    /// <summary>Creates a room through the real admin API, failing the test if the server refuses.</summary>
    public async Task CreateRoomAsync(
        string roomId,
        int maxPlayers = 16,
        int tickHz = 20,
        float aoiRadius = 1200f,
        int maxEntities = 256,
        int maxVisibleEntities = 64)
    {
        using AdminApiClient admin = CreateAdminClient();
        (bool created, string? error) = await admin.TryCreateRoomAsync(
            roomId, "tests", maxPlayers, tickHz, aoiRadius, maxEntities, maxVisibleEntities);
        Assert.True(created, $"room '{roomId}' was refused: {error}");
    }

    /// <summary>Connects a client to a room and waits for its welcome.</summary>
    public async Task<RoomClient> ConnectAsync(string roomId, string displayName, byte[]? resumeKey = null)
    {
        RoomClient client = new(BaseUri, roomId, displayName);
        await client.ConnectAsync(resumeKey);
        return client;
    }
}

/// <summary>
/// One server for every end-to-end test class, because starting Kestrel per test would dominate the
/// runtime and the tests are written to be independent through distinct room ids anyway.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LiveServerCollection : ICollectionFixture<LiveServerFixture>
{
    /// <summary>The xUnit collection name.</summary>
    public const string Name = "live-server";
}
