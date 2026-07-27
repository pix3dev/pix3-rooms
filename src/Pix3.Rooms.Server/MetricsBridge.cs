using System.Text;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Auth;
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
/// are the <b>stable dashboard contract</b> — alert on those. The <c>net_*_total</c> and
/// <c>room_*_total</c> series are raw per-<see cref="NetCounter"/> transport detail and raw room detail,
/// so the full picture is scrapeable without having to guess which raw counter feeds which mapped family.
/// </para>
/// </remarks>
public sealed class MetricsBridge : BackgroundService
{
    /// <summary>
    /// The per-room counters this bridge diffs, in the order their baselines are stored. Index 0 feeds the
    /// pre-existing <c>room_budget_overruns_total</c> family; the rest get a raw series of their own.
    /// </summary>
    /// <remarks>
    /// The first three come from <see cref="RoomStats"/> (any <see cref="IRoom"/> has them); the rest are
    /// published by the concrete <see cref="Room"/> and are sampled as 0 for any other implementation.
    /// </remarks>
    private enum RoomCounter
    {
        BudgetOverruns = 0,
        Resyncs,
        Violations,
        SkippedTicks,
        ResumesGranted,
        ResumeGracesStarted,
        ResumeGracesExpired,
        HostMigrations,
        SignalRejections,
        RefusedEntityUpdates,
    }

    /// <summary>How often one pass runs.</summary>
    private static readonly TimeSpan PumpInterval = TimeSpan.FromSeconds(1);

    /// <summary>Number of <see cref="RoomCounter"/> members.</summary>
    private const int RoomCounterCount = (int)RoomCounter.RefusedEntityUpdates + 1;

    /// <summary>
    /// <see cref="AuthFailureCause"/> (what the Auth module reports) to <see cref="AuthFailureReason"/>
    /// (what the metric is labelled with), indexed by cause ordinal.
    /// </summary>
    /// <remarks>
    /// The two enums are deliberate duplicates — <c>Auth</c> may not reference <c>Observability</c> — and
    /// they differ by exactly one member: <see cref="AuthFailureReason.ServiceTokenInvalid"/> has no cause,
    /// because <c>ServiceTokenEndpointFilter</c> counts its own refusals directly. Mapping only the causes
    /// is therefore also what keeps that label from being counted twice.
    /// </remarks>
    private static readonly AuthFailureReason[] AuthReasonByCause =
    [
        AuthFailureReason.MissingToken,
        AuthFailureReason.MalformedToken,
        AuthFailureReason.InvalidSignature,
        AuthFailureReason.Expired,
        AuthFailureReason.RoomMismatch,
        AuthFailureReason.Other,
    ];

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

    /// <summary>Last-seen inbound frame count per TypeId; feeds <c>messages_in_total{type}</c>.</summary>
    private readonly long[] _lastInboundByType = new long[NetMetrics.TypeIdSlotCount];

    /// <summary>Last-seen refusal count per <see cref="AuthFailureCause"/>.</summary>
    private readonly long[] _lastAuthFailures = new long[NetMetrics.AuthFailureSlotCount];

    /// <summary>Raw per-room series, indexed by <see cref="RoomCounter"/> ordinal.</summary>
    private readonly Counter[] _roomSeries;

    /// <summary>Last-seen per-room counter values, indexed by room id then <see cref="RoomCounter"/>.</summary>
    private readonly Dictionary<string, long[]> _lastRoomCounters = new(StringComparer.Ordinal);

    /// <summary>This room's freshly sampled counters. Reused across rooms and passes.</summary>
    private readonly long[] _roomSample = new long[RoomCounterCount];

    private readonly HashSet<string> _liveRoomIds = new(StringComparer.Ordinal);
    private readonly List<string> _staleRoomIds = [];

    private readonly Gauge _tickP50SecondsMax;
    private readonly Gauge _tickP99SecondsMax;
    private readonly Gauge _tickJitterP99SecondsMax;
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

        // Tick *start* jitter, which body time cannot show: a room can execute every tick in 300 us and
        // still start 15 ms late on a coarse-granularity platform, and it is the start times players feel.
        _tickJitterP99SecondsMax = registry.CreateGauge(
            "rooms_tick_jitter_p99_seconds_max",
            "Worst per-room 99th-percentile tick start jitter in seconds (max over all live rooms).");

        _bytesOutPerSecond = registry.CreateGauge(
            "rooms_bytes_out_per_second",
            "Outbound bytes per second summed over all live rooms.");

        _roomSeries = new Counter[RoomCounterCount];

        // Index 0 reuses the pre-existing dashboard family instead of declaring a second series with the
        // same name; every other room counter gets a raw series named like the transport's.
        _roomSeries[(int)RoomCounter.BudgetOverruns] = metrics.RoomBudgetOverrunsTotal;
        for (int i = 0; i < _roomSeries.Length; i++)
        {
            if (_roomSeries[i] is not null)
            {
                continue;
            }

            string member = ((RoomCounter)i).ToString();
            _roomSeries[i] = registry.CreateCounter(
                $"room_{ToSnakeCase(member)}_total",
                $"Room counter {member}, summed over every room that has ever run on this server.");
        }
    }

    /// <summary>
    /// Runs one full pass: diffs every transport counter into its mapped family and its raw series, then
    /// refreshes the room gauges. Idempotent with respect to counters — a second call with no traffic in
    /// between adds nothing.
    /// </summary>
    public void Pump()
    {
        PumpTransport();
        PumpInboundTypes();
        PumpRejects();
        PumpAuthFailures();
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

        // Still not fed from here: room_tick_duration_seconds. The histogram needs per-tick observations
        // from inside Room, and only the percentiles RoomStats already computes cross the seam — so tick
        // health is exported as the rooms_tick_* gauges instead.
    }

    /// <summary>
    /// Diffs the 256-slot per-TypeId inbound histogram into <c>messages_in_total{type}</c>. Labels come
    /// from <see cref="MessageTypeIds.GetName"/> (resolved once by <see cref="RoomsMetrics"/>), so they are
    /// the v2 class names and unmapped ids collapse into the shared <c>other</c> series.
    /// </summary>
    private void PumpInboundTypes()
    {
        for (int typeId = 0; typeId < _lastInboundByType.Length; typeId++)
        {
            long current = _net.GetInboundByType((byte)typeId);
            long delta = current - _lastInboundByType[typeId];
            if (delta <= 0)
            {
                continue;
            }

            _lastInboundByType[typeId] = current;
            _metrics.MessagesIn((byte)typeId).Add(delta);
        }
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

    /// <summary>
    /// Diffs the validators' per-cause refusal counts into <c>auth_failures_total{reason}</c>. Three very
    /// different stories — no token, unparseable, wrong signature — all reach the client as
    /// <c>InvalidToken</c>, and this is the only place they stay distinguishable.
    /// </summary>
    private void PumpAuthFailures()
    {
        for (int i = 0; i < AuthReasonByCause.Length; i++)
        {
            AuthFailureCause cause = (AuthFailureCause)i;
            long current = _net.GetAuthFailureCount(cause);
            long delta = current - _lastAuthFailures[i];
            if (delta <= 0)
            {
                continue;
            }

            _lastAuthFailures[i] = current;
            _metrics.AuthFailures(AuthReasonByCause[i]).Add(delta);
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
        double worstJitterMs = 0d;
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

            SampleRoomCounters(room, stats);
            AccumulateRoomCounters(roomId);

            if (stats.TickMsP50 > worstP50Ms)
            {
                worstP50Ms = stats.TickMsP50;
            }

            if (stats.TickMsP99 > worstP99Ms)
            {
                worstP99Ms = stats.TickMsP99;
            }

            if (stats.TickJitterMsP99 > worstJitterMs)
            {
                worstJitterMs = stats.TickJitterMsP99;
            }

            bytesOutPerSecond += stats.BytesOutPerSecond;
        }

        PruneDeadRooms();

        // RoomStats reports milliseconds; Prometheus convention (and the unfed histogram) is seconds.
        _tickP50SecondsMax.Set(worstP50Ms / 1000d);
        _tickP99SecondsMax.Set(worstP99Ms / 1000d);
        _tickJitterP99SecondsMax.Set(worstJitterMs / 1000d);
        _bytesOutPerSecond.Set(bytesOutPerSecond);
    }

    /// <summary>
    /// Fills <see cref="_roomSample"/> from one room. The first three values cross the <see cref="IRoom"/>
    /// seam through <see cref="RoomStats"/>; the rest are published by the concrete <see cref="Room"/> and
    /// stay zero for any other implementation, which is exactly right — a fake room has no host migrations.
    /// </summary>
    private void SampleRoomCounters(IRoom room, RoomStats stats)
    {
        long[] sample = _roomSample;
        sample[(int)RoomCounter.BudgetOverruns] = stats.BudgetOverruns;
        sample[(int)RoomCounter.Resyncs] = stats.Resyncs;
        sample[(int)RoomCounter.Violations] = stats.Violations;

        if (room is Room concrete)
        {
            sample[(int)RoomCounter.SkippedTicks] = concrete.SkippedTicks;
            sample[(int)RoomCounter.ResumesGranted] = concrete.ResumesGranted;
            sample[(int)RoomCounter.ResumeGracesStarted] = concrete.ResumeGracesStarted;
            sample[(int)RoomCounter.ResumeGracesExpired] = concrete.ResumeGracesExpired;
            sample[(int)RoomCounter.HostMigrations] = concrete.HostMigrations;
            sample[(int)RoomCounter.SignalRejections] = concrete.SignalRejections;
            sample[(int)RoomCounter.RefusedEntityUpdates] = concrete.RefusedEntityUpdates;
            return;
        }

        for (int i = (int)RoomCounter.SkippedTicks; i < sample.Length; i++)
        {
            sample[i] = 0;
        }
    }

    /// <summary>
    /// Adds this room's positive deltas to the process-wide series and re-bases its baseline.
    /// </summary>
    /// <remarks>
    /// Baselines are stored per room id and rewritten unconditionally: a room id reused after destruction
    /// restarts at zero, and the baseline has to follow it down or every later increment would be
    /// swallowed. <c>Violations</c> is not even monotonic within one room — it drops when a member
    /// leaves — which is the same case and is handled by the same rule.
    /// </remarks>
    private void AccumulateRoomCounters(string roomId)
    {
        if (!_lastRoomCounters.TryGetValue(roomId, out long[]? baseline))
        {
            baseline = new long[RoomCounterCount];
            _lastRoomCounters[roomId] = baseline;
        }

        long[] sample = _roomSample;
        for (int i = 0; i < sample.Length; i++)
        {
            long delta = sample[i] - baseline[i];
            if (delta > 0)
            {
                _roomSeries[i].Add(delta);
            }

            baseline[i] = sample[i];
        }
    }

    /// <summary>
    /// Forgets the baseline of every room that is gone, so a destroyed room's lifetime total can never be
    /// re-added by a later room that happens to reuse its id.
    /// </summary>
    private void PruneDeadRooms()
    {
        if (_lastRoomCounters.Count == _liveRoomIds.Count)
        {
            return;
        }

        _staleRoomIds.Clear();
        foreach (string roomId in _lastRoomCounters.Keys)
        {
            if (!_liveRoomIds.Contains(roomId))
            {
                _staleRoomIds.Add(roomId);
            }
        }

        for (int i = 0; i < _staleRoomIds.Count; i++)
        {
            _lastRoomCounters.Remove(_staleRoomIds[i]);
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
