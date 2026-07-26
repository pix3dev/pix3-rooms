using System.Text;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Net;
using Pix3.Rooms.Server.Observability;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server;

/// <summary>
/// Copies the transport's and the rooms' own counters into the Prometheus registry once a second.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NetMetrics"/> is deliberately dependency-free (the Net module must not know about
/// Observability) and <c>Room</c> exposes counters only through <see cref="RoomStats"/>, so this class is
/// the single place where transport and room numbers become scrapeable. It lives in the composition root
/// precisely because it is the seam, not a module concern.
/// </para>
/// <para>
/// Everything is <b>monotonic diffing</b>: last-seen values are kept in pre-sized arrays and only the
/// positive delta is added, because <see cref="Counter"/> refuses a negative amount. A source that went
/// backwards (a room id reused after destruction) resets the baseline instead of throwing.
/// </para>
/// <para>
/// Two overlapping views are exported on purpose. The <see cref="RoomsMetrics"/> families
/// (<c>connections_total</c>, <c>frames_dropped_total{reason}</c>, <c>quota_violations_total{kind}</c>, …)
/// are the <b>stable dashboard contract</b> — alert on those. The <c>net_*_total</c> series are raw
/// per-<see cref="NetCounter"/> transport detail, so the full picture is scrapeable without having to
/// guess which raw counter feeds which mapped family.
/// </para>
/// </remarks>
public sealed class MetricsBridge : BackgroundService
{
    /// <summary>How often one pass runs.</summary>
    private static readonly TimeSpan PumpInterval = TimeSpan.FromSeconds(1);

    private readonly NetMetrics _net;
    private readonly RoomsMetrics _metrics;
    private readonly IRoomManager _rooms;
    private readonly ILogger<MetricsBridge> _logger;

    /// <summary>Raw <c>net_*_total</c> series, indexed by <see cref="NetCounter"/> ordinal.</summary>
    private readonly Counter[] _netSeries;

    private readonly long[] _lastNetCounters;

    /// <summary>This pass's positive deltas, indexed like <see cref="_netSeries"/>. Reused every pass.</summary>
    private readonly long[] _netDeltas;

    /// <summary>Every defined <see cref="RejectCode"/> except <see cref="RejectCode.None"/>.</summary>
    private readonly RejectCode[] _rejectCodes;

    private readonly long[] _lastRejects;

    /// <summary>Last-seen <see cref="RoomStats.BudgetOverruns"/> per room id.</summary>
    private readonly Dictionary<string, long> _lastBudgetOverruns = new(StringComparer.Ordinal);

    private readonly HashSet<string> _liveRoomIds = new(StringComparer.Ordinal);
    private readonly List<string> _staleRoomIds = [];

    private readonly Gauge _tickP50SecondsMax;
    private readonly Gauge _tickP99SecondsMax;
    private readonly Gauge _bytesOutPerSecond;

    /// <summary>Creates the bridge and declares the raw transport and tick-health series.</summary>
    /// <param name="net">The transport counter surface to read.</param>
    /// <param name="metrics">The metric families to feed.</param>
    /// <param name="rooms">Room registry, for room gauges and per-room stats.</param>
    /// <param name="logger">Logger for pass failures.</param>
    public MetricsBridge(NetMetrics net, RoomsMetrics metrics, IRoomManager rooms, ILogger<MetricsBridge> logger)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(logger);

        _net = net;
        _metrics = metrics;
        _rooms = rooms;
        _logger = logger;

        _lastNetCounters = new long[NetMetrics.CounterCount];
        _netDeltas = new long[NetMetrics.CounterCount];
        _netSeries = new Counter[NetMetrics.CounterCount];

        MetricsRegistry registry = metrics.Registry;

        // Name conversion happens exactly once here; a pass must never format a string.
        for (int i = 0; i < _netSeries.Length; i++)
        {
            string member = ((NetCounter)i).ToString();
            _netSeries[i] = registry.CreateCounter(
                $"net_{ToSnakeCase(member)}_total",
                $"Raw transport counter NetCounter.{member}.");
        }

        RejectCode[] allCodes = Enum.GetValues<RejectCode>();
        List<RejectCode> reportable = new(allCodes.Length);
        for (int i = 0; i < allCodes.Length; i++)
        {
            if (allCodes[i] != RejectCode.None)
            {
                reportable.Add(allCodes[i]);
            }
        }

        _rejectCodes = [.. reportable];
        _lastRejects = new long[_rejectCodes.Length];

        // room_tick_duration_seconds cannot be fed from here (see Pump), so tick health is still visible
        // as the worst percentile across rooms plus the aggregate outbound rate.
        _tickP50SecondsMax = registry.CreateGauge(
            "rooms_tick_p50_seconds_max",
            "Worst per-room median tick duration in seconds (max over all live rooms).");

        _tickP99SecondsMax = registry.CreateGauge(
            "rooms_tick_p99_seconds_max",
            "Worst per-room 99th-percentile tick duration in seconds (max over all live rooms).");

        _bytesOutPerSecond = registry.CreateGauge(
            "rooms_bytes_out_per_second",
            "Outbound bytes per second summed over all live rooms.");
    }

    /// <summary>
    /// Runs one full pass: diffs every transport counter into its mapped family and its raw series, then
    /// refreshes the room gauges. Idempotent with respect to counters — a second call with no traffic in
    /// between adds nothing.
    /// </summary>
    public void Pump()
    {
        PumpTransport();
        PumpRejects();
        PumpRooms();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One pass up front so a scrape immediately after startup is not blank for a whole second.
        SafePump();

        using PeriodicTimer timer = new(PumpInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                SafePump();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        // A final pass so the last scrape before the process exits is not missing the tail of the run.
        SafePump();
    }

    private void SafePump()
    {
        try
        {
            Pump();
        }
        catch (Exception exception)
        {
            // A broken pass must never kill the bridge: metrics going stale is far worse than one gap.
            _logger.LogError(exception, "Metrics bridge pass failed; counters may be missing one interval.");
        }
    }

    private void PumpTransport()
    {
        RoomsMetrics metrics = _metrics;

        metrics.ConnectionsActive.Set(_net.CurrentConnections);

        long[] deltas = _netDeltas;
        for (int i = 0; i < deltas.Length; i++)
        {
            long current = _net.Get((NetCounter)i);
            long delta = current - _lastNetCounters[i];
            if (delta <= 0)
            {
                deltas[i] = 0;
                continue;
            }

            _lastNetCounters[i] = current;
            deltas[i] = delta;
            _netSeries[i].Add(delta);
        }

        metrics.ConnectionsTotal.Add(deltas[(int)NetCounter.ConnectionsAccepted]);

        // The transport counts socket writes, not send-queue enqueues. That is the nearest available
        // signal to "frames handed to a send queue" until Net grows a metrics seam of its own.
        metrics.MessagesOutTotal.Add(deltas[(int)NetCounter.OutboundFramesSent]);
        metrics.BytesOutTotal.Add(deltas[(int)NetCounter.OutboundBytesSent]);

        metrics.FramesDropped(FrameDropReason.OutboundQueueFull).Add(deltas[(int)NetCounter.OutboundDroppedQueueFull]);
        metrics.FramesDropped(FrameDropReason.InboundQueueFull).Add(deltas[(int)NetCounter.InboundDroppedRoomQueueFull]);
        metrics.FramesDropped(FrameDropReason.SendFailed).Add(deltas[(int)NetCounter.SendFailures]);

        metrics.ProtocolErrorsTotal.Add(
            deltas[(int)NetCounter.InboundMalformed]
            + deltas[(int)NetCounter.InboundUnknownTypeId]
            + deltas[(int)NetCounter.InboundServerOnlyTypeId]
            + deltas[(int)NetCounter.InboundTextFrames]);

        metrics.QuotaViolations(QuotaKind.MessageRate).Add(deltas[(int)NetCounter.QuotaMessageRateBreaches]);
        metrics.QuotaViolations(QuotaKind.ByteRate).Add(deltas[(int)NetCounter.QuotaByteRateBreaches]);
        metrics.QuotaViolations(QuotaKind.PayloadSize).Add(deltas[(int)NetCounter.QuotaPayloadBreaches]);
        metrics.QuotaViolations(QuotaKind.EntityUpdatesPerFrame).Add(deltas[(int)NetCounter.QuotaEntityUpdateBreaches]);
        metrics.QuotaViolations(QuotaKind.SpawnRate).Add(deltas[(int)NetCounter.QuotaSpawnBreaches]);
        metrics.QuotaViolations(QuotaKind.ChatRate).Add(deltas[(int)NetCounter.QuotaChatBreaches]);

        // Not fed from here, each waiting on its own module's metrics seam:
        // messages_in_total{type} — per-TypeId counting has to happen inside InboundDispatcher, which is
        //   the only place that knows a frame's TypeId; NetMetrics only totals InboundMessages.
        // auth_failures_total{reason} other than service_token_invalid — needs a seam in the Auth module;
        //   ServiceTokenEndpointFilter already counts its own refusals directly.
        // room_tick_duration_seconds — the histogram needs per-tick observations from inside Room; only
        //   the percentiles RoomStats already computes are exported (see the rooms_tick_* gauges).
    }

    private void PumpRejects()
    {
        for (int i = 0; i < _rejectCodes.Length; i++)
        {
            RejectCode code = _rejectCodes[i];
            long current = _net.GetRejectCount(code);
            long delta = current - _lastRejects[i];
            if (delta <= 0)
            {
                continue;
            }

            _lastRejects[i] = current;
            _metrics.ConnectionsRejected(code).Add(delta);
        }
    }

    private void PumpRooms()
    {
        _metrics.RefreshRoomGauges(_rooms);

        // RoomStats carries no room id, so the per-room baseline is joined through the registry rather
        // than trusting ListStats() ordering as an identity.
        IReadOnlyList<RoomConfig> configs = _rooms.ListConfigs();

        _liveRoomIds.Clear();
        double worstP50Ms = 0d;
        double worstP99Ms = 0d;
        long bytesOutPerSecond = 0;

        for (int i = 0; i < configs.Count; i++)
        {
            string roomId = configs[i].RoomId;
            if (!_rooms.TryGet(roomId, out IRoom? room))
            {
                // Destroyed between listing and lookup; its baseline is pruned below.
                continue;
            }

            _liveRoomIds.Add(roomId);
            RoomStats stats = room.SnapshotStats();

            _lastBudgetOverruns.TryGetValue(roomId, out long previousOverruns);
            long overrunDelta = stats.BudgetOverruns - previousOverruns;
            if (overrunDelta > 0)
            {
                _metrics.RoomBudgetOverrunsTotal.Add(overrunDelta);
            }

            // Assigned unconditionally: a room id reused after destruction restarts at zero, and the
            // baseline has to follow it down or every later overrun would be swallowed.
            _lastBudgetOverruns[roomId] = stats.BudgetOverruns;

            if (stats.TickMsP50 > worstP50Ms)
            {
                worstP50Ms = stats.TickMsP50;
            }

            if (stats.TickMsP99 > worstP99Ms)
            {
                worstP99Ms = stats.TickMsP99;
            }

            bytesOutPerSecond += stats.BytesOutPerSecond;
        }

        PruneDeadRooms();

        // RoomStats reports milliseconds; Prometheus convention (and the unfed histogram) is seconds.
        _tickP50SecondsMax.Set(worstP50Ms / 1000d);
        _tickP99SecondsMax.Set(worstP99Ms / 1000d);
        _bytesOutPerSecond.Set(bytesOutPerSecond);
    }

    /// <summary>
    /// Forgets the baseline of every room that is gone, so a destroyed room's lifetime total can never be
    /// re-added by a later room that happens to reuse its id.
    /// </summary>
    private void PruneDeadRooms()
    {
        if (_lastBudgetOverruns.Count == _liveRoomIds.Count)
        {
            return;
        }

        _staleRoomIds.Clear();
        foreach (string roomId in _lastBudgetOverruns.Keys)
        {
            if (!_liveRoomIds.Contains(roomId))
            {
                _staleRoomIds.Add(roomId);
            }
        }

        for (int i = 0; i < _staleRoomIds.Count; i++)
        {
            _lastBudgetOverruns.Remove(_staleRoomIds[i]);
        }
    }

    /// <summary>Turns <c>InboundDroppedRoomQueueFull</c> into <c>inbound_dropped_room_queue_full</c>.</summary>
    private static string ToSnakeCase(string value)
    {
        StringBuilder builder = new(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsAsciiLetterUpper(c))
            {
                bool previousIsLowerOrDigit = i > 0 && (char.IsAsciiLetterLower(value[i - 1]) || char.IsAsciiDigit(value[i - 1]));
                bool nextIsLower = i + 1 < value.Length && char.IsAsciiLetterLower(value[i + 1]);
                if (i > 0 && (previousIsLowerOrDigit || nextIsLower))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
