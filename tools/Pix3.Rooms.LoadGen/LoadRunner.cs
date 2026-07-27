using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;


namespace Pix3.Rooms.LoadGen;

/// <summary>Everything one run measured, in a shape both the text and the JSON report render from.</summary>
public sealed record LoadGenReport(
    LoadGenOptions Options,
    int ClientsRequested,
    int ClientsJoined,
    IReadOnlyList<string> JoinFailures,
    double HoldSeconds,
    long BytesReceived,
    long BytesSent,
    long FramesReceived,
    long FramesSent,
    long Enters,
    long Updates,
    long Removals,
    long UpdateBytes,
    long SnapshotFrames,
    long DeltaFrames,
    int PeakKnownCount,
    double KbitPerSecondPerClientMean,
    double KbitPerSecondPerClientP50,
    double KbitPerSecondPerClientP95,
    double KbitPerSecondPerClientMax,
    double RoundTripP50,
    double RoundTripP95,
    double RoundTripP99,
    int SeqGaps,
    int ResyncsRequested,
    int UpdatesForUnknownSlots,
    int RemovalsForUnknownSlots,
    int DuplicateEnters,
    int MalformedFrames,
    int SocketsClosedEarly,
    IReadOnlyList<RoomStatsSnapshot> RoomStats)
{
    /// <summary>
    /// True when nothing the clients saw invalidates the measurement. A run with a <c>Seq</c> gap, an
    /// undecodable frame or a delta for an unknown slot measured a stream a real client could not have
    /// followed, so its throughput number means nothing.
    /// </summary>
    public bool IsValidMeasurement =>
        SeqGaps == 0
        && UpdatesForUnknownSlots == 0
        && RemovalsForUnknownSlots == 0
        && DuplicateEnters == 0
        && MalformedFrames == 0
        && SocketsClosedEarly == 0
        && ClientsJoined == ClientsRequested;
}

/// <summary>
/// Drives N rooms × M clients against a live server and reports what it measured.
/// </summary>
/// <remarks>
/// <para>
/// The point of this tool is that a performance claim needs numbers rather than reasoning
/// (<c>AGENTS.md</c>). So it measures two independent sides and prints both: what the clients received
/// (bytes per client per second, record mix, round-trip) and what the server says about itself (tick
/// body p50/p99 and — the one that actually proves the scheduler — tick <i>start jitter</i> p99).
/// </para>
/// <para>
/// It also refuses to launder a broken run into a throughput figure. Every client validates the stream
/// it receives, and the report leads with a verdict: a run with a <c>Seq</c> gap or a delta for an
/// unknown slot is reported as an invalid measurement, not as a fast one.
/// </para>
/// </remarks>
public sealed class LoadRunner
{
    private readonly LoadGenOptions _options;
    private readonly Action<string> _log;

    /// <summary>Creates a runner.</summary>
    /// <param name="options">Validated run configuration.</param>
    /// <param name="log">Progress sink; defaults to stdout.</param>
    public LoadRunner(LoadGenOptions options, Action<string>? log = null)
    {
        _options = options;
        _log = log ?? Console.WriteLine;
    }

    /// <summary>Runs the whole thing: create rooms, ramp up, hold, collect, tear down.</summary>
    public async Task<LoadGenReport> RunAsync(CancellationToken cancellationToken = default)
    {
        using AdminApiClient admin = new(_options.BaseUri, _options.ServiceToken);
        string[] roomIds = Enumerable.Range(0, _options.Rooms)
            .Select(i => $"{_options.RoomPrefix}-{i}")
            .ToArray();

        if (_options.CreateRooms)
        {
            await CreateRoomsAsync(admin, roomIds, cancellationToken).ConfigureAwait(false);
        }

        List<RoomClient> clients = [];
        List<string> joinFailures = [];
        try
        {
            await JoinAsync(roomIds, clients, joinFailures, cancellationToken).ConfigureAwait(false);
            _log($"joined {clients.Count}/{_options.Rooms * _options.ClientsPerRoom} clients");

            if (clients.Count == 0)
            {
                throw new InvalidOperationException(
                    "no client joined — check the service token, the room ids, and the per-IP connection caps "
                    + "(Rooms:Quotas:MaxConnectionsPerIp defaults to 8).");
            }

            (double holdSeconds, long[] bytesAtStart) = await HoldAsync(clients, cancellationToken).ConfigureAwait(false);

            List<RoomStatsSnapshot> roomStats = [];
            foreach (string roomId in roomIds)
            {
                if (await admin.GetRoomStatsAsync(roomId, cancellationToken).ConfigureAwait(false) is { } stats)
                {
                    roomStats.Add(stats);
                }
            }

            return Summarize(clients, joinFailures, holdSeconds, bytesAtStart, roomStats);
        }
        finally
        {
            foreach (RoomClient client in clients)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            if (_options.CreateRooms)
            {
                foreach (string roomId in roomIds)
                {
                    await admin.DeleteRoomAsync(roomId, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task CreateRoomsAsync(AdminApiClient admin, string[] roomIds, CancellationToken cancellationToken)
    {
        foreach (string roomId in roomIds)
        {
            (bool created, string? error) = await admin.TryCreateRoomAsync(
                roomId,
                _options.ProjectId,
                _options.MaxPlayers,
                _options.TickHz,
                _options.AoiRadius,
                _options.MaxEntities,
                _options.MaxVisibleEntities,
                cancellationToken).ConfigureAwait(false);

            if (!created)
            {
                throw new InvalidOperationException($"could not create room '{roomId}': {error}");
            }
        }

        _log($"created {roomIds.Length} room(s) at {_options.TickHz} Hz, AOI {_options.AoiRadius}");
    }

    private async Task JoinAsync(
        string[] roomIds,
        List<RoomClient> clients,
        List<string> joinFailures,
        CancellationToken cancellationToken)
    {
        for (int room = 0; room < roomIds.Length; room++)
        {
            for (int index = 0; index < _options.ClientsPerRoom; index++)
            {
                string name = $"{roomIds[room]}-c{index}";
                RoomClient client = new(_options.BaseUri, roomIds[room], name);
                try
                {
                    await client.ConnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    (float x, float y) = StartPosition(index, room);
                    uint netId = await client.SpawnAsync(x, y, cancellationToken: cancellationToken).ConfigureAwait(false);
                    clients.Add(client);
                    _avatars[client] = netId;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    joinFailures.Add($"{name}: {exception.Message}");
                    await client.DisposeAsync().ConfigureAwait(false);
                }

                if (_options.JoinStaggerMs > 0)
                {
                    await Task.Delay(_options.JoinStaggerMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (joinFailures.Count > 0)
        {
            _log($"WARNING: {joinFailures.Count} client(s) failed to join; first: {joinFailures[0]}");
            _log("         if these are RateLimited, raise Rooms:Quotas:MaxConnectionsPerIp and "
                 + "Rooms:Server:MaxPreAuthConnectionsPerIp — the shipped defaults are 8 and 4 per address.");
        }
    }

    private readonly Dictionary<RoomClient, uint> _avatars = [];

    /// <summary>
    /// Holds the load for the configured duration, driving each client on its own loop. Byte counters are
    /// sampled at the start of the hold so the ramp-up traffic (snapshots for every joiner) does not get
    /// averaged into the steady-state figure.
    /// </summary>
    private async Task<(double HoldSeconds, long[] BytesAtStart)> HoldAsync(List<RoomClient> clients, CancellationToken cancellationToken)
    {
        long[] bytesAtStart = clients.Select(c => c.Metrics.BytesReceived).ToArray();
        Stopwatch clock = Stopwatch.StartNew();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(_options.DurationSeconds));

        _log($"holding {clients.Count} clients for {_options.DurationSeconds} s at {_options.SendHz} Hz "
             + $"({_options.Pattern.ToString().ToLowerInvariant()})");

        Task[] loops = new Task[clients.Count];
        for (int i = 0; i < clients.Count; i++)
        {
            RoomClient client = clients[i];
            int index = i;
            loops[i] = Task.Run(() => DriveAsync(client, index, stop.Token), CancellationToken.None);
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
        clock.Stop();
        return (clock.Elapsed.TotalSeconds, bytesAtStart);
    }

    private async Task DriveAsync(RoomClient client, int index, CancellationToken cancellationToken)
    {
        if (!_avatars.TryGetValue(client, out uint netId))
        {
            return;
        }

        TimeSpan period = TimeSpan.FromSeconds(1.0 / _options.SendHz);
        using PeriodicTimer timer = new(period);
        int step = 0;
        int sincePing = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                (float x, float y, float rot) = NextPosition(index, step++);
                await client.SendUpdateAsync(netId, x, y, rot, cancellationToken).ConfigureAwait(false);

                if (++sincePing >= _options.SendHz)
                {
                    sincePing = 0;
                    await client.PingAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The hold ended.
        }
        catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The socket died mid-run; the report counts it through CloseStatus.
        }
    }

    private (float X, float Y) StartPosition(int index, int room)
    {
        _ = room;
        return _options.Pattern switch
        {
            MovementPattern.Dogpile => (index % 20, index / 20f),
            MovementPattern.Drift => (-1500f + (index * 7f % 3000f), -1500f + (index * 13f % 3000f)),
            _ => OrbitCentre(index),
        };
    }

    private (float X, float Y, float Rot) NextPosition(int index, int step)
    {
        double t = step / (double)_options.SendHz;
        switch (_options.Pattern)
        {
            case MovementPattern.Dogpile:
            {
                // Everyone inside a 40-unit blob: every client is inside every other client's AOI, which is
                // the case the three caps exist for.
                double angle = (index * 0.37) + t;
                return ((float)(Math.Cos(angle) * (index % 40)), (float)(Math.Sin(angle) * (index % 40)), (float)angle);
            }

            case MovementPattern.Drift:
            {
                // Straight lines across the world, wrapped: a constant churn of AOI enters and exits.
                float x = (float)(-1500.0 + Mod((index * 7.0) + (t * 120.0), 3000.0));
                float y = (float)(-1500.0 + Mod((index * 13.0) + (t * 80.0), 3000.0));
                return (x, y, (float)(t % (Math.PI * 2)));
            }

            default:
            {
                (float cx, float cy) = OrbitCentre(index);
                double angle = t * 0.8;
                return ((float)(cx + (Math.Cos(angle) * 120.0)), (float)(cy + (Math.Sin(angle) * 120.0)), (float)angle);
            }
        }
    }

    private static double Mod(double value, double modulus)
    {
        double result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    /// <summary>Spreads orbit centres over a square grid inside the default 4096-unit world.</summary>
    private (float X, float Y) OrbitCentre(int index)
    {
        int perRow = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(_options.ClientsPerRoom)));
        float spacing = Math.Min(600f, 3000f / perRow);
        int column = index % perRow;
        int row = index / perRow;
        float origin = -(perRow - 1) * spacing / 2f;
        return (origin + (column * spacing), origin + (row * spacing));
    }

    private LoadGenReport Summarize(
        List<RoomClient> clients,
        List<string> joinFailures,
        double holdSeconds,
        long[] bytesAtStart,
        List<RoomStatsSnapshot> roomStats)
    {
        long bytesReceived = 0, bytesSent = 0, framesReceived = 0, framesSent = 0;
        long enters = 0, updates = 0, removals = 0, updateBytes = 0, snapshots = 0, deltas = 0;
        int peakKnown = 0, seqGaps = 0, resyncs = 0, unknownUpdates = 0, unknownRemovals = 0;
        int duplicateEnters = 0, malformed = 0, closedEarly = 0;
        List<double> perClientKbit = [];
        List<double> roundTrips = [];

        for (int i = 0; i < clients.Count; i++)
        {
            ClientMetrics m = clients[i].Metrics;
            bytesReceived += m.BytesReceived;
            bytesSent += m.BytesSent;
            framesReceived += m.FramesReceived;
            framesSent += m.FramesSent;
            enters += m.Enters;
            updates += m.Updates;
            removals += m.Removals;
            updateBytes += m.UpdateBytes;
            snapshots += m.SnapshotFrames;
            deltas += m.DeltaFrames;
            peakKnown = Math.Max(peakKnown, m.PeakKnownCount);
            seqGaps += m.SeqGaps;
            resyncs += m.ResyncsRequested;
            unknownUpdates += m.UpdatesForUnknownSlots;
            unknownRemovals += m.RemovalsForUnknownSlots;
            duplicateEnters += m.DuplicateEnters;
            malformed += m.MalformedFrames;
            if (m.CloseStatus is not null)
            {
                closedEarly++;
            }

            // Steady state only: the snapshot every joiner received during ramp-up is excluded.
            long steadyBytes = m.BytesReceived - bytesAtStart[i];
            perClientKbit.Add(holdSeconds > 0 ? steadyBytes * 8.0 / 1000.0 / holdSeconds : 0.0);
            roundTrips.AddRange(m.RoundTripSamples);
        }

        return new LoadGenReport(
            _options,
            _options.Rooms * _options.ClientsPerRoom,
            clients.Count,
            joinFailures,
            holdSeconds,
            bytesReceived,
            bytesSent,
            framesReceived,
            framesSent,
            enters,
            updates,
            removals,
            updateBytes,
            snapshots,
            deltas,
            peakKnown,
            perClientKbit.Count > 0 ? perClientKbit.Average() : 0.0,
            Percentile(perClientKbit, 0.50),
            Percentile(perClientKbit, 0.95),
            perClientKbit.Count > 0 ? perClientKbit.Max() : 0.0,
            Percentile(roundTrips, 0.50),
            Percentile(roundTrips, 0.95),
            Percentile(roundTrips, 0.99),
            seqGaps,
            resyncs,
            unknownUpdates,
            unknownRemovals,
            duplicateEnters,
            malformed,
            closedEarly,
            roomStats);
    }

    /// <summary>Nearest-rank percentile. Returns 0 for an empty sample rather than throwing.</summary>
    public static double Percentile(List<double> samples, double quantile)
    {
        if (samples.Count == 0)
        {
            return 0.0;
        }

        double[] sorted = samples.ToArray();
        Array.Sort(sorted);
        int rank = (int)Math.Ceiling(quantile * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    /// <summary>Renders the report for a human.</summary>
    public static string RenderText(LoadGenReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        CultureInfo culture = CultureInfo.InvariantCulture;
        StringBuilder text = new();

        text.AppendLine("--- pix3-rooms load run ---------------------------------------------");
        text.AppendLine(string.Create(culture,
            $"config      {report.Options.Rooms} room(s) x {report.Options.ClientsPerRoom} clients, "
            + $"{report.Options.Pattern.ToString().ToLowerInvariant()}, {report.Options.SendHz} Hz sends, "
            + $"{report.Options.TickHz} Hz rooms, AOI {report.Options.AoiRadius}, "
            + $"maxVisible {report.Options.MaxVisibleEntities}"));
        text.AppendLine(string.Create(culture,
            $"clients     {report.ClientsJoined}/{report.ClientsRequested} joined, held {report.HoldSeconds:0.0} s"));

        foreach (string failure in report.JoinFailures.Take(3))
        {
            text.AppendLine($"  join fail {failure}");
        }

        text.AppendLine();
        text.AppendLine("client side (steady state, WebSocket payload only, framing excluded)");
        text.AppendLine(string.Create(culture,
            $"  egress    mean {report.KbitPerSecondPerClientMean:0.0} kbit/s per client, "
            + $"p50 {report.KbitPerSecondPerClientP50:0.0}, p95 {report.KbitPerSecondPerClientP95:0.0}, "
            + $"max {report.KbitPerSecondPerClientMax:0.0}"));
        text.AppendLine(string.Create(culture,
            $"  totals    {report.BytesReceived / 1024.0 / 1024.0:0.00} MiB in / "
            + $"{report.BytesSent / 1024.0 / 1024.0:0.00} MiB out, "
            + $"{report.FramesReceived} frames in / {report.FramesSent} out"));
        text.AppendLine(string.Create(culture,
            $"  records   {report.Enters} enters, {report.Updates} updates, {report.Removals} removals; "
            + $"mean update record {(report.Updates > 0 ? report.UpdateBytes / (double)report.Updates : 0):0.0} B"));
        text.AppendLine(string.Create(culture,
            $"  frames    {report.SnapshotFrames} snapshot, {report.DeltaFrames} delta; "
            + $"peak known set {report.PeakKnownCount} (cap {report.Options.MaxVisibleEntities})"));
        text.AppendLine(string.Create(culture,
            $"  rtt       p50 {report.RoundTripP50:0.0} ms, p95 {report.RoundTripP95:0.0} ms, p99 {report.RoundTripP99:0.0} ms"));

        text.AppendLine();
        text.AppendLine("server side (admin API)");
        foreach (RoomStatsSnapshot room in report.RoomStats)
        {
            text.AppendLine(string.Create(culture,
                $"  tick      body p50 {room.TickMsP50:0.000} ms / p99 {room.TickMsP99:0.000} ms, "
                + $"start jitter p99 {room.TickJitterMsP99:0.000} ms"));
            text.AppendLine(string.Create(culture,
                $"  room      {room.PlayerCount} players, {room.EntityCount} entities, tick {room.ServerTick}, "
                + $"{room.BytesOutPerSecond / 1000.0:0.0} kB/s out"));
            text.AppendLine(string.Create(culture,
                $"  faults    {room.DroppedFrames} dropped frames, {room.BudgetOverruns} budget overruns, "
                + $"{room.Resyncs} resyncs, {room.Violations} violations"));
        }

        text.AppendLine();
        if (report.IsValidMeasurement)
        {
            text.AppendLine("verdict     VALID - every client followed its stream with no gaps and no protocol violations");
        }
        else
        {
            text.AppendLine("verdict     INVALID MEASUREMENT - the numbers above describe a stream a real client could not follow:");
            AppendFault(text, "Seq gaps", report.SeqGaps);
            AppendFault(text, "updates for unknown slots", report.UpdatesForUnknownSlots);
            AppendFault(text, "removals for unknown slots", report.RemovalsForUnknownSlots);
            AppendFault(text, "duplicate enters", report.DuplicateEnters);
            AppendFault(text, "malformed frames", report.MalformedFrames);
            AppendFault(text, "sockets closed early", report.SocketsClosedEarly);
            AppendFault(text, "clients that never joined", report.ClientsRequested - report.ClientsJoined);
        }

        if (report.ResyncsRequested > 0)
        {
            text.AppendLine(string.Create(culture, $"            {report.ResyncsRequested} resync(s) requested after those gaps"));
        }

        return text.ToString();
    }

    private static void AppendFault(StringBuilder text, string label, int count)
    {
        if (count > 0)
        {
            text.AppendLine($"            {count} {label}");
        }
    }

    /// <summary>Renders the report as JSON, for a CI job that wants to track the numbers over time.</summary>
    public static string RenderJson(LoadGenReport report)
        => JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
}
