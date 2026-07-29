using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Pix3.Rooms.Server.Net;
using Pix3.Rooms.Server.Observability;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server.Admin;

/// <summary>
/// <c>GET /admin/stats</c> — the fabric's own view of itself: version, resource use, transport counters
/// and every live room, in one service-token-authenticated response.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the two endpoints that came before it answer different questions. <c>/health</c> is
/// the unauthenticated liveness probe and must stay a one-line answer. <c>/metrics</c> is a Prometheus
/// scrape target — the right surface for alerting, the wrong one for a dashboard that would otherwise have
/// to parse exposition text and re-derive per-room rows from label sets.
/// </para>
/// <para>
/// Everything here is control path: one <c>Process</c> read, four small <c>/proc</c> reads and a walk over
/// the room registry, per request. Nothing on the tick path is touched, and no room state is mutated —
/// counters are read through <see cref="IRoom.SnapshotStats"/>, which is the seam that exists for this.
/// </para>
/// </remarks>
public static class ServerStatsEndpoints
{
    /// <summary>Route of the stats endpoint, relative to the admin group prefix.</summary>
    public const string StatsRoute = "/stats";

    /// <summary>
    /// Maps <c>GET /admin/stats</c> onto an existing admin group, so it inherits that group's
    /// <see cref="ServiceTokenEndpointFilter"/> instead of declaring its own auth.
    /// </summary>
    /// <param name="group">The admin group returned by <see cref="RoomAdminEndpoints.MapRoomAdminApi"/>.</param>
    public static RouteHandlerBuilder MapServerStatsEndpoint(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return group.MapGet(StatsRoute, ServerStats).WithName("ServerStats");
    }

    private static Ok<ServerStatsResponse> ServerStats(
        IRoomManager manager,
        RoomsMetrics metrics,
        ResourceSampler sampler,
        NetOptions netOptions,
        IOptions<RoomServerOptions> roomServerOptions,
        IHostEnvironment environment)
    {
        ResourceSnapshot resources = sampler.Sample();

        return TypedResults.Ok(new ServerStatsResponse(
            HealthEndpoints.HealthyStatus,
            ServerRuntimeInfo.Version,
            ServerRuntimeInfo.Commit,
            environment.EnvironmentName,
            ServerRuntimeInfo.StartedAt,
            ServerRuntimeInfo.UptimeSeconds,
            Describe(resources.Process),
            Describe(resources.Host),
            DescribeConnections(metrics, netOptions),
            DescribeRooms(manager, roomServerOptions.Value.MaxRooms)));
    }

    private static ProcessStatsResponse Describe(ProcessResources process)
        => new(
            process.Pid,
            process.CpuPercent,
            process.CpuSecondsTotal,
            process.WorkingSetBytes,
            process.PrivateMemoryBytes,
            process.ManagedHeapBytes,
            process.HeapLimitBytes,
            process.ThreadCount,
            process.Gen2Collections);

    private static HostStatsResponse Describe(HostResources host)
        => new(
            host.Hostname,
            host.Os,
            host.CpuCount,
            host.CpuPercent,
            host.Load1,
            host.Load5,
            host.Load15,
            host.MemoryTotalBytes,
            host.MemoryAvailableBytes,
            host.DiskTotalBytes,
            host.DiskFreeBytes,
            host.UptimeSeconds);

    /// <summary>
    /// Reads the transport totals off the Prometheus families.
    /// </summary>
    /// <remarks>
    /// <see cref="MetricsBridge"/> pumps <c>NetMetrics</c> into these once a second, so the numbers here
    /// are at most one second behind the sockets — the same freshness <c>/health</c> already reports, and
    /// far cheaper than giving <c>Admin</c> a second dependency on <c>Net</c> for the raw counters.
    /// </remarks>
    private static ConnectionStatsResponse DescribeConnections(RoomsMetrics metrics, NetOptions netOptions)
        => new(
            ToCount(metrics.ConnectionsActive.Value),
            netOptions.MaxTotalConnections,
            metrics.ConnectionsTotal.Value,
            SumSeries(metrics.ConnectionsRejectedTotal),
            SumSeries(metrics.AuthFailuresTotal),
            metrics.ProtocolErrorsTotal.Value,
            SumSeries(metrics.MessagesInTotal),
            metrics.MessagesOutTotal.Value,
            metrics.BytesOutTotal.Value,
            SumSeries(metrics.FramesDroppedTotal));

    private static RoomsStatsResponse DescribeRooms(IRoomManager manager, int maxRooms)
    {
        IReadOnlyList<RoomConfig> configs = manager.ListConfigs();
        List<RoomSummaryResponse> items = new(configs.Count);

        int players = 0;
        int entities = 0;
        long bytesOutPerSecond = 0;
        long droppedFrames = 0;
        double worstP50 = 0d;
        double worstP99 = 0d;
        double worstJitter = 0d;

        // RoomStats carries no room id, so join through the registry rather than trusting list ordering —
        // the same rule RoomAdminEndpoints.ListRooms and MetricsBridge follow.
        for (int i = 0; i < configs.Count; i++)
        {
            RoomConfig config = configs[i];
            if (!manager.TryGet(config.RoomId, out IRoom? room))
            {
                // Destroyed between listing and lookup; it simply is not part of this snapshot.
                continue;
            }

            RoomStats stats = room.SnapshotStats();

            players += stats.PlayerCount;
            entities += stats.EntityCount;
            bytesOutPerSecond += stats.BytesOutPerSecond;
            droppedFrames += stats.DroppedFrames;
            worstP50 = Math.Max(worstP50, stats.TickMsP50);
            worstP99 = Math.Max(worstP99, stats.TickMsP99);
            worstJitter = Math.Max(worstJitter, stats.TickJitterMsP99);

            items.Add(new RoomSummaryResponse(
                config.RoomId,
                config.ProjectId,
                config.BuildId,
                config.Mode.ToString(),
                stats.PlayerCount,
                config.MaxPlayers,
                stats.EntityCount,
                config.TickHz,
                stats.TickMsP50,
                stats.TickMsP99,
                stats.BytesOutPerSecond,
                stats.DroppedFrames,
                stats.Resyncs,
                stats.Violations,
                room.CreatedAt,
                room.LastActivityAt));
        }

        return new RoomsStatsResponse(
            items.Count,
            maxRooms,
            players,
            entities,
            bytesOutPerSecond,
            worstP50,
            worstP99,
            worstJitter,
            droppedFrames,
            items);
    }

    /// <summary>
    /// Total of a counter family. An unlabelled counter renders itself as its own single series, so the
    /// same walk covers both shapes and a labelled family's parent value (always zero) is never read.
    /// </summary>
    private static long SumSeries(Counter family)
    {
        IReadOnlyList<Metric> series = family.Series;
        long total = 0;
        for (int i = 0; i < series.Count; i++)
        {
            if (series[i] is Counter counter)
            {
                total += counter.Value;
            }
        }

        return total;
    }

    private static int ToCount(double gaugeValue)
    {
        if (double.IsNaN(gaugeValue) || gaugeValue <= 0d)
        {
            return 0;
        }

        return gaugeValue >= int.MaxValue ? int.MaxValue : (int)gaugeValue;
    }
}
