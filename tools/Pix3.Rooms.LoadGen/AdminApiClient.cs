using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pix3.Rooms.LoadGen;

/// <summary>One room's server-side counters, as the admin API reports them.</summary>
/// <remarks>
/// Mirrors <c>RoomStatsResponse</c> field for field but is declared here rather than referenced, because
/// this tool depends on <c>Pix3.Rooms.Protocol</c> only — the load generator is a client, and a client
/// does not link the server. <c>TickJitterMsP99</c> is the number that proves the tick loop works: tick
/// body time can be perfect while starts jitter by 15 ms.
/// </remarks>
public sealed record RoomStatsSnapshot(
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

/// <summary>The subset of the admin REST API a load run needs: create, inspect and destroy rooms.</summary>
public sealed class AdminApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>Creates a client for one server.</summary>
    /// <param name="baseUri">The server's HTTP base address.</param>
    /// <param name="serviceToken">
    /// The <c>Rooms:Auth:ServiceToken</c> value. Without it the admin API denies everything, which is the
    /// documented behaviour of an empty token and not a bug to work around.
    /// </param>
    public AdminApiClient(Uri baseUri, string serviceToken)
    {
        _http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Add("X-Service-Token", serviceToken);
    }

    /// <summary>Creates a room. Returns false when the server refused; the message carries its reason.</summary>
    public async Task<(bool Created, string? Error)> TryCreateRoomAsync(
        string roomId,
        string projectId,
        int maxPlayers,
        int tickHz,
        float aoiRadius,
        int maxEntities,
        int maxVisibleEntities,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            roomId,
            projectId,
            maxPlayers,
            tickHz,
            aoiRadius,
            maxEntities,
            maxVisibleEntities,
        };

        using HttpResponseMessage response =
            await _http.PostAsJsonAsync("/admin/rooms", request, JsonOptions, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return (true, null);
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (false, $"HTTP {(int)response.StatusCode}: {body}");
    }

    /// <summary>Reads one room's counters, or null when the room is gone.</summary>
    public async Task<RoomStatsSnapshot?> GetRoomStatsAsync(string roomId, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response =
            await _http.GetAsync($"/admin/rooms/{Uri.EscapeDataString(roomId)}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        JsonElement stats = document.RootElement.GetProperty("stats");
        return stats.Deserialize<RoomStatsSnapshot>(JsonOptions);
    }

    /// <summary>Destroys a room. False when it did not exist.</summary>
    public async Task<bool> DeleteRoomAsync(string roomId, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response =
            await _http.DeleteAsync($"/admin/rooms/{Uri.EscapeDataString(roomId)}", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>The raw Prometheus exposition text, for a run that wants process-wide numbers too.</summary>
    public async Task<string> GetMetricsAsync(string path = "/metrics", CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
