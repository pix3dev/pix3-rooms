using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Auth;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Everything the transport counts. One value per member; the enum ordinal is the array index, so a
/// counter bump is a single <see cref="Interlocked.Add(ref long, long)"/> with no dictionary lookup and
/// no allocation.
/// </summary>
/// <remarks>Append new members at the end — the ordinals are an implementation detail, but reordering
/// them churns every dashboard built on the exported names.</remarks>
public enum NetCounter
{
    /// <summary>WebSocket upgrades completed.</summary>
    ConnectionsAccepted = 0,

    /// <summary>Requests to the endpoint that were not WebSocket upgrades (answered 400).</summary>
    ConnectionsRejectedNotWebSocket,

    /// <summary>Upgrades refused because <see cref="NetOptions.MaxTotalConnections"/> was reached.</summary>
    ConnectionsRejectedServerCap,

    /// <summary>Upgrades refused because <see cref="QuotaOptions.MaxConnectionsPerIp"/> was reached.</summary>
    ConnectionsRejectedIpCap,

    /// <summary>Upgrades refused because the address tracking table was saturated.</summary>
    ConnectionsRejectedUntrackable,

    /// <summary>Connections that finished their lifetime (successful or not).</summary>
    ConnectionsClosed,

    /// <summary>Handshakes that produced a <c>WelcomeEvent</c>.</summary>
    HandshakesSucceeded,

    /// <summary>Handshakes refused for any reason (see the per-reject-code counters for which).</summary>
    HandshakesRejected,

    /// <summary>Connections dropped for never sending <c>HelloCommand</c> in time.</summary>
    HandshakeTimeouts,

    /// <summary>Handshakes refused by the per-IP join throttle.</summary>
    JoinThrottleBreaches,

    /// <summary>Complete inbound frames received after the handshake.</summary>
    InboundMessages,

    /// <summary>Bytes in those frames.</summary>
    InboundBytes,

    /// <summary>Frames handed to a room's inbound queue.</summary>
    InboundEnqueued,

    /// <summary>Frames dropped because the room's inbound queue was full.</summary>
    InboundDroppedRoomQueueFull,

    /// <summary>Frames whose TypeId this build does not know. Ignored, never fatal.</summary>
    InboundUnknownTypeId,

    /// <summary>Frames whose TypeId is server-to-client only (a client must never send it).</summary>
    InboundServerOnlyTypeId,

    /// <summary>Frames in the app-reserved range 192-255, which this server deliberately ignores.</summary>
    InboundAppRangeIgnored,

    /// <summary>Frames that failed structural validation (truncated record, illegal mask, bad payload).</summary>
    InboundMalformed,

    /// <summary>Text frames received; the protocol is binary only, so each one closes the connection.</summary>
    InboundTextFrames,

    /// <summary>Messages that exceeded <see cref="QuotaOptions.MaxPayloadBytes"/>.</summary>
    InboundOversized,

    /// <summary>Consecutive-protocol-error cutoffs (connection closed for sustained garbage).</summary>
    ProtocolErrorCutoffs,

    /// <summary><c>PingCommand</c>s answered on the socket thread, without a room round-trip.</summary>
    PingsAnswered,

    /// <summary><c>LeaveCommand</c>s honoured.</summary>
    LeaveCommands,

    /// <summary>Connections closed by the per-connection message-rate limit.</summary>
    QuotaMessageRateBreaches,

    /// <summary>Connections closed by the per-connection byte-rate limit.</summary>
    QuotaByteRateBreaches,

    /// <summary>Connections closed for an over-sized payload.</summary>
    QuotaPayloadBreaches,

    /// <summary><c>EntityUpdatePacket</c>s dropped for declaring more records than the quota allows.</summary>
    QuotaEntityUpdateBreaches,

    /// <summary>Spawn requests dropped by the per-connection spawn throttle.</summary>
    QuotaSpawnBreaches,

    /// <summary>Chat messages dropped by the per-connection chat throttle.</summary>
    QuotaChatBreaches,

    /// <summary>Connections closed for exceeding <see cref="QuotaOptions.IdleTimeoutSeconds"/>.</summary>
    IdleTimeouts,

    /// <summary>Frames written to a socket.</summary>
    OutboundFramesSent,

    /// <summary>Bytes written to sockets.</summary>
    OutboundBytesSent,

    /// <summary>
    /// Frames refused because a connection's outbound queue was full, on either lane. The aggregate the
    /// dashboard's <c>frames_dropped_total{outbound_queue_full}</c> family is fed from; see
    /// <see cref="OutboundControlQueueOverflows"/> and <see cref="OutboundHotQueueOverflows"/> for the
    /// split, which is what actually tells you whether a client is dying or merely stale.
    /// </summary>
    OutboundDroppedQueueFull,

    /// <summary>Sends that failed because the peer went away mid-write.</summary>
    SendFailures,

    /// <summary>Upgrades refused because the request's <c>Origin</c> is not on the allowlist.</summary>
    ConnectionsRejectedOrigin,

    /// <summary>
    /// Upgrades refused because the address already holds
    /// <see cref="NetOptions.MaxPreAuthConnectionsPerIp"/> unauthenticated sockets.
    /// </summary>
    ConnectionsRejectedPreAuthCap,

    /// <summary>Upgrades refused by the per-IP new-connection rate bucket.</summary>
    ConnectionsRejectedConnectRate,

    /// <summary>
    /// Connections closed for sending more than <see cref="NetOptions.MaxPreAuthFrameBytes"/> before
    /// authenticating.
    /// </summary>
    PreAuthFrameOversized,

    /// <summary>
    /// Connections closed for sending a second frame before authenticating. Exactly one pre-auth frame is
    /// accepted.
    /// </summary>
    PreAuthExtraFrames,

    /// <summary>
    /// Handshakes refused because the client announced a version below
    /// <see cref="ProtocolVersion.MinSupported"/>.
    /// </summary>
    ProtocolVersionMismatches,

    /// <summary>Sessions re-attached to a dropped session inside its resume grace.</summary>
    ResumesSucceeded,

    /// <summary>
    /// Presented resume keys that matched no pending session and therefore degraded to a fresh join. Not an
    /// error path — a stale key is simply not a resume.
    /// </summary>
    ResumesFallbackToJoin,

    /// <summary>
    /// Control frames refused because the control lane was full. Each one also closes the connection: a
    /// control frame has no repair mechanism, so a client that cannot drain it is unrecoverably behind.
    /// </summary>
    OutboundControlQueueOverflows,

    /// <summary>
    /// Hot frames refused because the hot lane was full. Each one costs the caller a known-set rollback and
    /// a resync, and nothing else — this is the recoverable overflow.
    /// </summary>
    OutboundHotQueueOverflows,

    /// <summary><c>ResyncCommand</c>s accepted and forwarded to a room.</summary>
    ResyncRequests,

    /// <summary><c>SetClientPrefsCommand</c>s accepted and forwarded to a room.</summary>
    ClientPrefsUpdates,

    /// <summary>Signals dropped by the <see cref="QuotaOptions.MaxSignalsToServerPerSecond"/> throttle.</summary>
    QuotaSignalToServerBreaches,

    /// <summary>Signals dropped by the <see cref="QuotaOptions.MaxSignalsToAoiPerSecond"/> throttle.</summary>
    QuotaSignalToAoiBreaches,

    /// <summary>
    /// Signals dropped by the <see cref="QuotaOptions.MaxSignalsToAllPerSecond"/> throttle — the 600x
    /// amplifier, and the counter worth alerting on.
    /// </summary>
    QuotaSignalToAllBreaches,

    /// <summary>Resync requests dropped by the <see cref="QuotaOptions.MaxResyncPerSecond"/> throttle.</summary>
    QuotaResyncBreaches,

    /// <summary>Update records carrying the teleport mask bit.</summary>
    TeleportBitsSeen,

    /// <summary>
    /// Teleport bits beyond <see cref="QuotaOptions.MaxTeleportsPerMinute"/>. Counted only — the record is
    /// still applied, because a respawn genuinely is a discontinuity under client authority.
    /// </summary>
    QuotaTeleportBreaches,
}

/// <summary>
/// The transport's counter surface. Deliberately dependency-free: <c>Net</c> must not know about the
/// Observability module, so the Prometheus exporter reads these values instead of being injected here.
/// Every method is safe to call from any thread and allocation-free.
/// </summary>
/// <remarks>
/// It implements <see cref="IAuthFailureSink"/> so the Auth validators can report <i>why</i> a token was
/// refused without depending on metrics or on this module — <c>Net -> Auth</c> is a declared dependency
/// arrow, and the reverse is not.
/// </remarks>
public sealed class NetMetrics : IAuthFailureSink
{
    /// <summary>Number of <see cref="NetCounter"/> members, i.e. the counter array length.</summary>
    public const int CounterCount = (int)NetCounter.QuotaTeleportBreaches + 1;

    /// <summary>
    /// Length of the per-reject-code histogram: one slot per <see cref="RejectCode"/> value plus one
    /// catch-all slot for codes a future protocol version might add.
    /// </summary>
    /// <remarks>
    /// Must track the <b>highest</b> defined <see cref="RejectCode"/>. Leaving it behind does not break
    /// anything loudly — the newest code silently shares the catch-all bucket and its dashboard line stays
    /// flat at zero — so update it in the same commit that adds a code.
    /// </remarks>
    public const int RejectSlotCount = (int)RejectCode.SendQueueOverflow + 2;

    /// <summary>Slots in the per-TypeId inbound histogram: one per possible <c>u8</c> TypeId.</summary>
    public const int TypeIdSlotCount = 256;

    /// <summary>Slots in the auth-failure histogram: one per <see cref="AuthFailureCause"/>.</summary>
    public const int AuthFailureSlotCount = (int)AuthFailureCause.Other + 1;

    private readonly long[] _counters = new long[CounterCount];
    private readonly long[] _rejects = new long[RejectSlotCount];

    /// <summary>
    /// Inbound frame count per TypeId. Feeds <c>messages_in_total{type}</c>, which nothing else can
    /// produce: the dispatcher is the only place that knows a frame's TypeId, and by the time a frame
    /// reaches a room it is opaque bytes again. A flat 256-slot array (2 KiB) rather than a dictionary
    /// keeps the bump to one indexed interlocked add on the inbound hot path.
    /// </summary>
    private readonly long[] _inboundByType = new long[TypeIdSlotCount];

    /// <summary>
    /// Refused-token counts per <see cref="AuthFailureCause"/>. Feeds <c>auth_failures_total{reason}</c>,
    /// which only the validators and the handshake can distinguish: three very different failures all reach
    /// the client as <see cref="RejectCode.InvalidToken"/>.
    /// </summary>
    private readonly long[] _authFailures = new long[AuthFailureSlotCount];

    private long _currentConnections;

    /// <summary>Live connections right now (a gauge, not a counter).</summary>
    public long CurrentConnections => Volatile.Read(ref _currentConnections);

    /// <summary>Adds <paramref name="amount"/> to a counter.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="counter"/> is not a defined member.</exception>
    public void Add(NetCounter counter, long amount)
    {
        int index = (int)counter;
        if ((uint)index >= (uint)_counters.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(counter), counter, "Undefined net counter.");
        }

        Interlocked.Add(ref _counters[index], amount);
    }

    /// <summary>Adds one to a counter.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="counter"/> is not a defined member.</exception>
    public void Increment(NetCounter counter) => Add(counter, 1);

    /// <summary>Reads a counter.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="counter"/> is not a defined member.</exception>
    public long Get(NetCounter counter)
    {
        int index = (int)counter;
        if ((uint)index >= (uint)_counters.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(counter), counter, "Undefined net counter.");
        }

        return Volatile.Read(ref _counters[index]);
    }

    /// <summary>Records that a session was refused with <paramref name="code"/>.</summary>
    public void OnReject(RejectCode code) => Interlocked.Increment(ref _rejects[RejectSlot(code)]);

    /// <summary>How many sessions were refused with <paramref name="code"/>.</summary>
    public long GetRejectCount(RejectCode code) => Volatile.Read(ref _rejects[RejectSlot(code)]);

    /// <summary>
    /// Records one inbound frame of <paramref name="typeId"/>. Called for every frame the dispatcher sees,
    /// including unknown and app-range ids, <b>before</b> any quota or validity decision — the point is to
    /// see what a client sends, not what the server accepted.
    /// </summary>
    /// <remarks>Every <c>byte</c> is a valid slot, so this can never be out of range and never throws.</remarks>
    public void OnInbound(byte typeId) => Interlocked.Increment(ref _inboundByType[typeId]);

    /// <summary>Inbound frames received with <paramref name="typeId"/>.</summary>
    public long GetInboundByType(byte typeId) => Volatile.Read(ref _inboundByType[typeId]);

    /// <inheritdoc />
    public void OnAuthFailure(AuthFailureCause cause) => Interlocked.Increment(ref _authFailures[AuthFailureSlot(cause)]);

    /// <summary>Tokens refused for <paramref name="cause"/>.</summary>
    public long GetAuthFailureCount(AuthFailureCause cause) => Volatile.Read(ref _authFailures[AuthFailureSlot(cause)]);

    /// <summary>Records a newly registered live connection.</summary>
    public void OnConnectionOpened() => Interlocked.Increment(ref _currentConnections);

    /// <summary>Records a connection leaving the live set.</summary>
    public void OnConnectionClosed() => Interlocked.Decrement(ref _currentConnections);

    private static int RejectSlot(RejectCode code)
    {
        int index = (int)code;
        // Unknown/future codes land in the catch-all slot rather than throwing on a network-driven path.
        return (uint)index < (uint)(RejectSlotCount - 1) ? index : RejectSlotCount - 1;
    }

    private static int AuthFailureSlot(AuthFailureCause cause)
    {
        int index = (int)cause;
        // Collapses onto Other, which is the enum's declared catch-all, for the same reason: a metrics
        // call must never be able to fail an authentication decision.
        return (uint)index < (uint)AuthFailureSlotCount ? index : (int)AuthFailureCause.Other;
    }
}
