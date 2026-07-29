using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pix3.Rooms.LoadGen;

namespace Pix3.Rooms.Tests.EndToEnd;

/// <summary>
/// <c>GET /admin/stats</c> against the real composition root: the auth it inherits, and the shape a
/// dashboard binds to.
/// </summary>
/// <remarks>
/// The endpoint is mapped onto the admin group specifically so it inherits the service-token filter, and
/// that inheritance is an absence of code — nothing in <c>ServerStatsEndpoints</c> mentions auth. An
/// absence needs a test, which is why the unauthenticated case is asserted here rather than assumed.
/// </remarks>
[Collection(LiveServerCollection.Name)]
public sealed class ServerStatsEndpointTests
{
    private readonly LiveServerFixture _server;

    public ServerStatsEndpointTests(LiveServerFixture server) => _server = server;

    [Fact]
    public async Task The_stats_endpoint_refuses_a_request_without_the_service_token()
    {
        using HttpClient http = new() { BaseAddress = _server.BaseUri };

        using HttpResponseMessage response = await http.GetAsync("/admin/stats");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected 401/403, saw {(int)response.StatusCode}");
    }

    [Fact]
    public async Task The_stats_endpoint_reports_the_process_the_host_and_every_live_room()
    {
        await _server.CreateRoomAsync("e2e-stats-endpoint", maxPlayers: 12, tickHz: 20);
        await using RoomClient ada = await _server.ConnectAsync("e2e-stats-endpoint", "ada");
        await ada.SpawnAsync(0f, 0f);

        using HttpClient http = new() { BaseAddress = _server.BaseUri };
        http.DefaultRequestHeaders.Add("X-Service-Token", LiveServerFixture.ServiceToken);

        JsonElement stats = await GetStatsWithRoomAsync(http, "e2e-stats-endpoint");

        Assert.Equal("ok", stats.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(stats.GetProperty("version").GetString()));
        Assert.True(stats.GetProperty("uptimeSeconds").GetDouble() >= 0d);

        JsonElement process = stats.GetProperty("process");
        Assert.Equal(Environment.ProcessId, process.GetProperty("pid").GetInt32());
        Assert.True(process.GetProperty("workingSetBytes").GetInt64() > 0);

        JsonElement host = stats.GetProperty("host");
        Assert.Equal(Environment.MachineName, host.GetProperty("hostname").GetString());
        Assert.Equal(Environment.ProcessorCount, host.GetProperty("cpuCount").GetInt32());

        // The connection this test holds open is the one the transport counters must already know about.
        JsonElement connections = stats.GetProperty("connections");
        Assert.True(connections.GetProperty("maxTotal").GetInt32() > 0);
        Assert.True(connections.GetProperty("acceptedTotal").GetInt64() > 0);

        JsonElement rooms = stats.GetProperty("rooms");
        Assert.True(rooms.GetProperty("count").GetInt32() >= 1);
        Assert.True(rooms.GetProperty("maxRooms").GetInt32() > 0);

        JsonElement room = FindRoom(rooms, "e2e-stats-endpoint");
        Assert.Equal("tests", room.GetProperty("projectId").GetString());
        Assert.Equal(12, room.GetProperty("maxPlayers").GetInt32());
        Assert.Equal(20, room.GetProperty("tickHz").GetInt32());
        Assert.Equal(1, room.GetProperty("players").GetInt32());
        Assert.Equal(1, room.GetProperty("entities").GetInt32());
    }

    /// <summary>
    /// Polls until both asynchronous sources have caught up: the room publishes its counters from its own
    /// tick thread, and the transport totals arrive through <c>MetricsBridge</c>'s one-second pump. Reading
    /// once, immediately, would legitimately see zero entities and zero accepted connections.
    /// </summary>
    private static async Task<JsonElement> GetStatsWithRoomAsync(HttpClient http, string roomId)
    {
        JsonElement stats = default;
        for (int attempt = 0; attempt < 200; attempt++)
        {
            JsonDocument? document = await http.GetFromJsonAsync<JsonDocument>("/admin/stats");
            Assert.NotNull(document);
            stats = document!.RootElement.Clone();

            JsonElement room = FindRoom(stats.GetProperty("rooms"), roomId);
            bool roomIsLive = room.ValueKind != JsonValueKind.Undefined && room.GetProperty("entities").GetInt32() > 0;
            bool transportPumped = stats.GetProperty("connections").GetProperty("acceptedTotal").GetInt64() > 0;
            if (roomIsLive && transportPumped)
            {
                return stats;
            }

            await Task.Delay(20);
        }

        return stats;
    }

    private static JsonElement FindRoom(JsonElement rooms, string roomId)
    {
        foreach (JsonElement item in rooms.GetProperty("items").EnumerateArray())
        {
            if (string.Equals(item.GetProperty("roomId").GetString(), roomId, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return default;
    }
}
