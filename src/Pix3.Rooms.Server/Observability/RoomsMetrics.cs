using System.Globalization;
using System.Text;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server.Observability;

/// <summary>
/// Strongly typed facade over the metrics this server exposes. Register it as a singleton and hand it to
/// Net, Rooms, Replication and Auth; nothing else should create metric families.
/// </summary>
/// <remarks>
/// <para>
/// Every labelled series is resolved once in the constructor and cached in a small array indexed by the
/// label enum (or by TypeId byte). Recording is therefore an array read plus one
/// <see cref="Interlocked"/> operation — no string work, no dictionary lookup, no allocation — which is
/// what makes these safe to call per message and per tick.
/// </para>
/// <para>
/// Label cardinality is fixed at construction: unknown enum values and unknown TypeIds collapse into the
/// shared <c>other</c> series, so no client input can create a new series.
/// </para>
/// </remarks>
public sealed class RoomsMetrics
{
    private const string UnknownTypeName = "Unknown";

    /// <summary>The collapse label, kept identical to the registry's overflow value.</summary>
    private static readonly string OtherLabel = Metric<Counter>.OverflowLabelValue;

    private readonly Counter[] _connectionsRejected;
    private readonly Counter _connectionsRejectedOther;
    private readonly Counter[] _messagesIn;
    private readonly Counter[] _framesDropped;
    private readonly Counter _framesDroppedOther;
    private readonly Counter[] _quotaViolations;
    private readonly Counter _quotaViolationsOther;
    private readonly Counter[] _authFailures;
    private readonly Counter _authFailuresOther;

    /// <summary>Declares every metric family this server exposes.</summary>
    /// <param name="registry">Registry to declare into; a fresh one is created when omitted.</param>
    public RoomsMetrics(MetricsRegistry? registry = null)
    {
        Registry = registry ?? new MetricsRegistry();

        RoomsActive = Registry.CreateGauge(
            "rooms_active",
            "Rooms currently alive on this server.");

        RoomPlayers = Registry.CreateGauge(
            "room_players",
            "Members currently joined, summed over all rooms.");

        EntitiesActive = Registry.CreateGauge(
            "entities_active",
            "Live replicated entities, summed over all rooms.");

        ConnectionsActive = Registry.CreateGauge(
            "connections_active",
            "WebSocket connections currently open.");

        ConnectionsTotal = Registry.CreateCounter(
            "connections_total",
            "WebSocket connections accepted since start.");

        ConnectionsRejectedTotal = Registry.CreateCounter(
            "connections_rejected_total",
            "Connections refused before or during the handshake, by reject code.",
            "reason");

        MessagesInTotal = Registry.CreateCounter(
            "messages_in_total",
            "Inbound frames accepted, by message type.",
            "type");

        MessagesOutTotal = Registry.CreateCounter(
            "messages_out_total",
            "Frames handed to a connection send queue.");

        BytesOutTotal = Registry.CreateCounter(
            "bytes_out_total",
            "Payload bytes handed to connection send queues.");

        FramesDroppedTotal = Registry.CreateCounter(
            "frames_dropped_total",
            "Frames discarded instead of delivered, by reason.",
            "reason");

        RoomTickDurationSeconds = Registry.CreateHistogram(
            "room_tick_duration_seconds",
            "Wall-clock duration of one room tick.",
            Histogram.DefaultTickDurationBuckets);

        RoomBudgetOverrunsTotal = Registry.CreateCounter(
            "room_budget_overruns_total",
            "Room ticks that exceeded their time budget.");

        QuotaViolationsTotal = Registry.CreateCounter(
            "quota_violations_total",
            "Client actions refused by a quota, by kind.",
            "kind");

        AuthFailuresTotal = Registry.CreateCounter(
            "auth_failures_total",
            "Token validations that failed, by reason.",
            "reason");

        ProtocolErrorsTotal = Registry.CreateCounter(
            "protocol_errors_total",
            "Frames that violated the wire protocol (undecodable, out of order, wrong plane).");

        _connectionsRejectedOther = ConnectionsRejectedTotal.WithLabels(OtherLabel);
        _connectionsRejected = BuildEnumSeries<RejectCode>(ConnectionsRejectedTotal, _connectionsRejectedOther);

        _framesDroppedOther = FramesDroppedTotal.WithLabels(OtherLabel);
        _framesDropped = BuildEnumSeries<FrameDropReason>(FramesDroppedTotal, _framesDroppedOther);

        _quotaViolationsOther = QuotaViolationsTotal.WithLabels(OtherLabel);
        _quotaViolations = BuildEnumSeries<QuotaKind>(QuotaViolationsTotal, _quotaViolationsOther);

        _authFailuresOther = AuthFailuresTotal.WithLabels(OtherLabel);
        _authFailures = BuildEnumSeries<AuthFailureReason>(AuthFailuresTotal, _authFailuresOther);

        _messagesIn = BuildMessageTypeSeries(MessagesInTotal);
    }

    /// <summary>The registry these metrics are declared in; what <c>/metrics</c> renders.</summary>
    public MetricsRegistry Registry { get; }

    /// <summary><c>rooms_active</c>: rooms currently alive.</summary>
    public Gauge RoomsActive { get; }

    /// <summary><c>room_players</c>: members joined across all rooms.</summary>
    public Gauge RoomPlayers { get; }

    /// <summary><c>entities_active</c>: live entities across all rooms.</summary>
    public Gauge EntitiesActive { get; }

    /// <summary><c>connections_active</c>: open WebSocket connections.</summary>
    public Gauge ConnectionsActive { get; }

    /// <summary><c>connections_total</c>: connections accepted since start.</summary>
    public Counter ConnectionsTotal { get; }

    /// <summary><c>connections_rejected_total{reason}</c> family; use <see cref="ConnectionsRejected"/>.</summary>
    public Counter ConnectionsRejectedTotal { get; }

    /// <summary><c>messages_in_total{type}</c> family; use <see cref="MessagesIn"/>.</summary>
    public Counter MessagesInTotal { get; }

    /// <summary><c>messages_out_total</c>: frames enqueued for sending.</summary>
    public Counter MessagesOutTotal { get; }

    /// <summary><c>bytes_out_total</c>: bytes enqueued for sending.</summary>
    public Counter BytesOutTotal { get; }

    /// <summary><c>frames_dropped_total{reason}</c> family; use <see cref="FramesDropped"/>.</summary>
    public Counter FramesDroppedTotal { get; }

    /// <summary><c>room_tick_duration_seconds</c>: per-tick duration histogram.</summary>
    public Histogram RoomTickDurationSeconds { get; }

    /// <summary><c>room_budget_overruns_total</c>: ticks over budget.</summary>
    public Counter RoomBudgetOverrunsTotal { get; }

    /// <summary><c>quota_violations_total{kind}</c> family; use <see cref="QuotaViolations"/>.</summary>
    public Counter QuotaViolationsTotal { get; }

    /// <summary><c>auth_failures_total{reason}</c> family; use <see cref="AuthFailures"/>.</summary>
    public Counter AuthFailuresTotal { get; }

    /// <summary><c>protocol_errors_total</c>: wire-protocol violations.</summary>
    public Counter ProtocolErrorsTotal { get; }

    /// <summary>Pre-resolved <c>connections_rejected_total{reason}</c> series. Unknown codes collapse to <c>other</c>.</summary>
    public Counter ConnectionsRejected(RejectCode reason)
    {
        Counter[] series = _connectionsRejected;
        uint index = (uint)reason;
        return index < (uint)series.Length ? series[index] : _connectionsRejectedOther;
    }

    /// <summary>Pre-resolved <c>messages_in_total{type}</c> series. Unmapped TypeIds collapse to <c>other</c>.</summary>
    public Counter MessagesIn(byte typeId) => _messagesIn[typeId];

    /// <summary>Pre-resolved <c>frames_dropped_total{reason}</c> series.</summary>
    public Counter FramesDropped(FrameDropReason reason)
    {
        Counter[] series = _framesDropped;
        uint index = (uint)reason;
        return index < (uint)series.Length ? series[index] : _framesDroppedOther;
    }

    /// <summary>Pre-resolved <c>quota_violations_total{kind}</c> series.</summary>
    public Counter QuotaViolations(QuotaKind kind)
    {
        Counter[] series = _quotaViolations;
        uint index = (uint)kind;
        return index < (uint)series.Length ? series[index] : _quotaViolationsOther;
    }

    /// <summary>Pre-resolved <c>auth_failures_total{reason}</c> series.</summary>
    public Counter AuthFailures(AuthFailureReason reason)
    {
        Counter[] series = _authFailures;
        uint index = (uint)reason;
        return index < (uint)series.Length ? series[index] : _authFailuresOther;
    }

    /// <summary>Counts one frame handed to a send queue, with its byte size.</summary>
    /// <param name="byteCount">Frame length in bytes.</param>
    public void RecordFrameOut(int byteCount)
    {
        MessagesOutTotal.Inc();
        if (byteCount > 0)
        {
            BytesOutTotal.Add(byteCount);
        }
    }

    /// <summary>Records one completed room tick, and an overrun when it blew its budget.</summary>
    /// <param name="stopwatchTicks">Elapsed <see cref="System.Diagnostics.Stopwatch"/> ticks for the tick.</param>
    /// <param name="overBudget">True when the tick exceeded its budget.</param>
    public void RecordTick(long stopwatchTicks, bool overBudget)
    {
        RoomTickDurationSeconds.ObserveStopwatchTicks(stopwatchTicks);
        if (overBudget)
        {
            RoomBudgetOverrunsTotal.Inc();
        }
    }

    /// <summary>
    /// Refreshes the room-scoped gauges from the live registry. Called right before a scrape so
    /// <c>rooms_active</c>, <c>room_players</c> and <c>entities_active</c> need no bridging from the
    /// Rooms module.
    /// </summary>
    /// <param name="manager">The room registry to read.</param>
    public void RefreshRoomGauges(IRoomManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        IReadOnlyList<RoomStats> stats = manager.ListStats();
        long players = 0;
        long entities = 0;
        for (int i = 0; i < stats.Count; i++)
        {
            RoomStats snapshot = stats[i];
            players += snapshot.PlayerCount;
            entities += snapshot.EntityCount;
        }

        RoomsActive.Set(manager.RoomCount);
        RoomPlayers.Set(players);
        EntitiesActive.Set(entities);
    }

    private static Counter[] BuildEnumSeries<TEnum>(Counter family, Counter fallback)
        where TEnum : struct, Enum
    {
        TEnum[] values = Enum.GetValues<TEnum>();
        int max = 0;
        for (int i = 0; i < values.Length; i++)
        {
            int numeric = ToInt32(values[i]);
            if (numeric > max)
            {
                max = numeric;
            }
        }

        Counter[] series = new Counter[max + 1];
        for (int i = 0; i < series.Length; i++)
        {
            series[i] = fallback;
        }

        for (int i = 0; i < values.Length; i++)
        {
            int numeric = ToInt32(values[i]);
            if (numeric < 0)
            {
                continue;
            }

            string label = ToSnakeCase(values[i].ToString());
            series[numeric] = string.Equals(label, OtherLabel, StringComparison.Ordinal)
                ? fallback
                : family.WithLabels(label);
        }

        return series;
    }

    private static Counter[] BuildMessageTypeSeries(Counter family)
    {
        Counter fallback = family.WithLabels(OtherLabel);
        Counter[] series = new Counter[256];
        for (int id = 0; id < series.Length; id++)
        {
            string name = MessageTypeIds.GetName((byte)id);
            series[id] = string.Equals(name, UnknownTypeName, StringComparison.Ordinal)
                ? fallback
                : family.WithLabels(ToSnakeCase(name));
        }

        return series;
    }

    private static int ToInt32<TEnum>(TEnum value)
        where TEnum : struct, Enum
        => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    /// <summary>Turns <c>ProtocolVersionMismatch</c> into <c>protocol_version_mismatch</c>. Setup path only.</summary>
    private static string ToSnakeCase(string value)
    {
        if (value.Length == 0)
        {
            return OtherLabel;
        }

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
