using Pix3.Rooms.Protocol;

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

    /// <summary>Connections dropped for never sending <c>HelloRequest</c> in time.</summary>
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

    /// <summary>Frames whose TypeId this build does not know.</summary>
    InboundUnknownTypeId,

    /// <summary>Frames whose TypeId is server-to-client only (a client must never send it).</summary>
    InboundServerOnlyTypeId,

    /// <summary>Frames in the app-reserved range 192–255, which this server deliberately ignores.</summary>
    InboundAppRangeIgnored,

    /// <summary>Frames that failed structural validation (truncated record, illegal mask, bad payload).</summary>
    InboundMalformed,

    /// <summary>Text frames received; the protocol is binary only, so each one closes the connection.</summary>
    InboundTextFrames,

    /// <summary>Messages that exceeded <see cref="QuotaOptions.MaxPayloadBytes"/>.</summary>
    InboundOversized,

    /// <summary>Consecutive-protocol-error cutoffs (connection closed for sustained garbage).</summary>
    ProtocolErrorCutoffs,

    /// <summary><c>PingRequest</c>s answered on the socket thread, without a room round-trip.</summary>
    PingsAnswered,

    /// <summary><c>LeaveRequest</c>s honoured.</summary>
    LeaveRequests,

    /// <summary>Connections closed by the per-connection message-rate limit.</summary>
    QuotaMessageRateBreaches,

    /// <summary>Connections closed by the per-connection byte-rate limit.</summary>
    QuotaByteRateBreaches,

    /// <summary>Connections closed for an over-sized payload.</summary>
    QuotaPayloadBreaches,

    /// <summary><c>EntityUpdateFrame</c>s dropped for declaring more records than the quota allows.</summary>
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

    /// <summary>Frames dropped because a connection's outbound queue was full (slow client).</summary>
    OutboundDroppedQueueFull,

    /// <summary>Sends that failed because the peer went away mid-write.</summary>
    SendFailures,
}

/// <summary>
/// The transport's counter surface. Deliberately dependency-free: <c>Net</c> must not know about the
/// Observability module, so the Prometheus exporter reads these values instead of being injected here.
/// Every method is safe to call from any thread and allocation-free.
/// </summary>
public sealed class NetMetrics
{
    /// <summary>Number of <see cref="NetCounter"/> members, i.e. the counter array length.</summary>
    public const int CounterCount = (int)NetCounter.SendFailures + 1;

    /// <summary>
    /// Length of the per-reject-code histogram: one slot per <see cref="RejectCode"/> value plus one
    /// catch-all slot for codes a future protocol version might add.
    /// </summary>
    public const int RejectSlotCount = (int)RejectCode.InternalError + 2;

    private readonly long[] _counters = new long[CounterCount];
    private readonly long[] _rejects = new long[RejectSlotCount];
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
}
