using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Per-connection and per-IP abuse limits, bound from configuration section <c>Rooms:Quotas</c>. The
/// defaults are the quota table in <c>docs/protocol.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every limit is a hard ceiling, not a target: a well-behaved client at 20 Hz sits far below all of
/// them. Setting a limit to zero or a negative number disables it — useful for load tests, never in
/// production.
/// </para>
/// <para>
/// <b>Per-connection only.</b> Per-entity and per-room limits (cold props per entity, entities per owner,
/// room vars, chat length) live in the Rooms module, which is the only place that knows about entities and
/// rooms. Two keys here (<see cref="MaxColdPropsPerSecond"/>, <see cref="MaxChatPerMinute"/>) are read by
/// that module as well; see their remarks.
/// </para>
/// </remarks>
public sealed class QuotaOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Rooms:Quotas";

    /// <summary>
    /// Simultaneous connections allowed from one client address. Enforced before the WebSocket upgrade
    /// is accepted, so a flood costs the server nothing but an HTTP 429.
    /// </summary>
    public int MaxConnectionsPerIp { get; set; } = 8;

    /// <summary>
    /// Sustained inbound frames per second per connection. Burst allowance is the same number, so a
    /// client may send one second's worth back-to-back. Breach closes with <c>RateLimited</c>.
    /// </summary>
    /// <remarks>
    /// 60/s is three times what a 20 Hz client needs (one <c>EntityUpdatePacket</c> per tick), leaving room
    /// for pings, chat and signals without leaving room for a flood.
    /// </remarks>
    public int MaxMessagesPerSecond { get; set; } = 60;

    /// <summary>
    /// Sustained inbound bytes per second per connection, with a one-second burst allowance. Breach
    /// closes with <c>RateLimited</c>.
    /// </summary>
    /// <remarks>
    /// 8 KiB/s. An owning client's real cost is its own entities' quantized updates — a handful of 15-byte
    /// records per tick, well under 1 KiB/s — so this is generous for play and tight against a client
    /// trying to push cold props or signals as a data channel.
    /// </remarks>
    public int MaxBytesPerSecond { get; set; } = 8_192;

    /// <summary>
    /// Largest single frame accepted in either direction. Also the size of a joined connection's receive
    /// buffer, so a client cannot grow server memory by streaming continuation frames. Breach closes with
    /// <c>PayloadTooLarge</c>.
    /// </summary>
    /// <remarks>
    /// 4 KiB matches the control-frame ceiling in the protocol's frame-size invariant. Hot frames are
    /// bounded far below it (1100 B, one MSS). An unauthenticated socket gets a much smaller buffer — see
    /// <see cref="NetOptions.MaxPreAuthFrameBytes"/>.
    /// </remarks>
    public int MaxPayloadBytes { get; set; } = 4_096;

    /// <summary>
    /// Largest <c>Count</c> accepted in one <c>EntityUpdatePacket</c>. An over-sized frame is dropped
    /// and counted; the connection survives, because a mis-sized batch is a client bug, not an attack.
    /// </summary>
    /// <remarks>
    /// 8 records. A client publishes what it owns, and <c>MaxEntitiesPerOwner</c> is the real ceiling on
    /// that; batching more than 8 in one tick means a client is either buggy or speaking for entities it
    /// does not own, and the ownership check would reject those anyway.
    /// </remarks>
    public int MaxEntityUpdatesPerFrame { get; set; } = 8;

    /// <summary>Spawn requests one connection may make per minute. Excess requests are dropped and counted.</summary>
    /// <remarks>240/min is 4/s sustained — a respawn loop, not a spawn cannon.</remarks>
    public int MaxSpawnsPerMinute { get; set; } = 240;

    /// <summary>
    /// <c>EmitSignalCommand</c>s per second with <see cref="SignalTarget.Server"/>. These fan out to
    /// nobody, so the limit only bounds room-thread work.
    /// </summary>
    public int MaxSignalsToServerPerSecond { get; set; } = 20;

    /// <summary>
    /// <c>EmitSignalCommand</c>s per second with <see cref="SignalTarget.AoiPeers"/> — the batched hot path
    /// a shooter's fire events take.
    /// </summary>
    /// <remarks>
    /// Tighter than the server target because each one is copied into every nearby client's
    /// <c>SignalBatchPacket</c>; the AOI cap bounds "nearby", and this bounds the rate.
    /// </remarks>
    public int MaxSignalsToAoiPerSecond { get; set; } = 10;

    /// <summary>
    /// <c>EmitSignalCommand</c>s per second with <see cref="SignalTarget.AllPeers"/>.
    /// </summary>
    /// <remarks>
    /// <b>2/s, the tightest quota in the table, because this one is a 600x amplifier</b>: one client's emit
    /// becomes one <c>SignalEvent</c> frame per member, on the control lane, with no AOI filtering to bound
    /// it. <see cref="SignalTarget.SinglePeer"/> shares this budget — it is the same unfiltered path with a
    /// single recipient, and a client that wants a peer-to-peer channel should be using it at conversation
    /// rate, not at tick rate.
    /// </remarks>
    public int MaxSignalsToAllPerSecond { get; set; } = 2;

    /// <summary>
    /// Cold-prop writes per second <b>per entity</b>. Enforced by the Rooms module, which is the only
    /// place that knows which entity a <c>SetEntityPropsCommand</c> names; declared here because it is a
    /// client-facing quota and belongs in the quota table.
    /// </summary>
    public int MaxColdPropsPerSecond { get; set; } = 2;

    /// <summary>
    /// <c>ResyncCommand</c>s per second per connection. Excess requests are dropped and counted.
    /// </summary>
    /// <remarks>
    /// A resync costs a full snapshot (~800 B) and restarts a continuation cursor, so it is the one client
    /// request that can ask the server for real work. 2/s is enough for a genuine <c>Seq</c> gap plus a tab
    /// refocus and far too little to use as a bandwidth pump.
    /// </remarks>
    public int MaxResyncPerSecond { get; set; } = 2;

    /// <summary>
    /// Teleport mask bits one connection may set per minute. <b>Soft: counted, never enforced.</b>
    /// </summary>
    /// <remarks>
    /// The teleport bit is legitimate under client authority — a respawn genuinely is a discontinuity — so
    /// dropping the record would break real gameplay. At Level 2 the server owns position and the bit is
    /// stripped rather than rationed; until then this builds the dataset that says which clients abuse it.
    /// </remarks>
    public int MaxTeleportsPerMinute { get; set; } = 12;

    /// <summary>
    /// Seconds without a single inbound frame before a joined connection is closed with
    /// <c>IdleTimeout</c>. Clients keep this alive with <c>PingCommand</c>, which the protocol
    /// documents as proof of liveness.
    /// </summary>
    public int IdleTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Chat messages one connection may send per minute. Excess messages are dropped and counted.
    /// </summary>
    /// <remarks>
    /// The Rooms module enforces its own room-level twin (<c>Rooms:Server:MaxChatPerMinute</c>) plus the
    /// length cap; this one stops a single connection before its frames ever reach the room thread.
    /// </remarks>
    public int MaxChatPerMinute { get; set; } = 10;

    /// <summary>
    /// Throws when a value would break an invariant the transport relies on. Called by the composition
    /// root right after binding.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is outside its supported range.</exception>
    public void Validate()
    {
        // The receive buffer is sized from this, and it must hold at least the largest hand-packed
        // frame header plus a record, otherwise legitimate traffic could never arrive.
        if (MaxPayloadBytes < 1024)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(MaxPayloadBytes)} must be at least 1024.");
        }

        if (MaxPayloadBytes > 1 << 20)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(MaxPayloadBytes)} must not exceed 1048576 (1 MiB).");
        }

        if (MaxEntityUpdatesPerFrame is < 1 or > HotWire.MaxEntityUpdateRecords)
        {
            // The wire count field is a single byte.
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxEntityUpdatesPerFrame)} must be between 1 and {HotWire.MaxEntityUpdateRecords}.");
        }

        // The remaining limits are all "zero or negative disables", so only an absurd magnitude is an
        // error. Each is still checked, because a silently ignored typo in a quota is how a limit stops
        // existing without anyone noticing.
        RequireSane(nameof(MaxConnectionsPerIp), MaxConnectionsPerIp, 1 << 16);
        RequireSane(nameof(MaxMessagesPerSecond), MaxMessagesPerSecond, 1 << 20);
        RequireSane(nameof(MaxBytesPerSecond), MaxBytesPerSecond, 1 << 28);
        RequireSane(nameof(MaxSpawnsPerMinute), MaxSpawnsPerMinute, 1 << 20);
        RequireSane(nameof(MaxSignalsToServerPerSecond), MaxSignalsToServerPerSecond, 1 << 20);
        RequireSane(nameof(MaxSignalsToAoiPerSecond), MaxSignalsToAoiPerSecond, 1 << 20);
        RequireSane(nameof(MaxSignalsToAllPerSecond), MaxSignalsToAllPerSecond, 1 << 20);
        RequireSane(nameof(MaxColdPropsPerSecond), MaxColdPropsPerSecond, 1 << 20);
        RequireSane(nameof(MaxResyncPerSecond), MaxResyncPerSecond, 1 << 20);
        RequireSane(nameof(MaxTeleportsPerMinute), MaxTeleportsPerMinute, 1 << 20);
        RequireSane(nameof(IdleTimeoutSeconds), IdleTimeoutSeconds, 24 * 60 * 60);
        RequireSane(nameof(MaxChatPerMinute), MaxChatPerMinute, 1 << 20);
    }

    /// <summary>
    /// Refuses a value above <paramref name="maximum"/>. Values at or below zero pass: that is how a quota
    /// is deliberately disabled.
    /// </summary>
    private static void RequireSane(string key, int value, int maximum)
    {
        if (value > maximum)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{key} is {value}, which exceeds the supported maximum of {maximum}. "
                + "Use zero to disable the limit instead of an implausibly large number.");
        }
    }
}
