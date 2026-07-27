namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Transport-level knobs, bound from configuration section <c>Rooms:Server</c>. A plain POCO on
/// purpose: the composition root binds and registers it, nothing here reaches into DI.
/// </summary>
/// <remarks>
/// The <c>Rooms:Server</c> section also carries the Rooms module's keys (<c>MaxRooms</c>,
/// <c>MaxDrainPerTick</c>, …), which are deliberately absent here — binding ignores unknown keys, so both
/// modules can bind the same section.
/// </remarks>
public sealed class NetOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Rooms:Server";

    /// <summary>Largest <see cref="MaxPreAuthFrameBytes"/> that could ever be useful.</summary>
    private const int MaxAllowedPreAuthFrameBytes = 64 * 1024;

    /// <summary>
    /// Smallest <see cref="MaxPreAuthFrameBytes"/> that can still hold a <c>HelloCommand</c> with a JWT.
    /// </summary>
    private const int MinAllowedPreAuthFrameBytes = 256;

    /// <summary>
    /// Capacity of a room's inbound queue. Owned by the Rooms module (it creates the queue) but
    /// configured here because it is a transport back-pressure knob.
    /// </summary>
    public int InboundQueueCapacity { get; set; } = 1024;

    /// <summary>
    /// Capacity of a connection's <see cref="FrameLane.Control"/> queue: handshake, chat, room vars, spawn
    /// responses, per-recipient signals, rejections.
    /// </summary>
    /// <remarks>
    /// Deep enough that a burst of joins, a room-var broadcast and a chat line never collide, because a
    /// full control lane <b>closes the connection</b> — a control frame has no later frame that repairs it,
    /// so refusing one is a session-ending event rather than a hiccup.
    /// </remarks>
    public int OutboundControlQueueCapacity { get; set; } = 64;

    /// <summary>
    /// Capacity of a connection's <see cref="FrameLane.Hot"/> queue: snapshots, deltas, signal batches.
    /// </summary>
    /// <remarks>
    /// Deliberately shallow. A deep hot queue only buffers <i>staleness</i>: by the time a client drains
    /// the tenth queued delta the entities in it have moved on, and it would have been cheaper to send one
    /// fresh snapshot. So overflow is treated as a resync signal — the frame's known-set changes are rolled
    /// back and the client is re-snapshotted — rather than something to absorb.
    /// </remarks>
    public int OutboundHotQueueCapacity { get; set; } = 8;

    /// <summary>
    /// Hard cap on simultaneously accepted WebSocket connections across all rooms. Checked before the
    /// upgrade is accepted; beyond it the endpoint answers 503.
    /// </summary>
    public int MaxTotalConnections { get; set; } = 4096;

    /// <summary>
    /// Kestrel's cap on upgraded (WebSocket) connections, applied by the composition root to
    /// <c>KestrelServerLimits</c>. Declared here so the key binds and validates with the rest of the
    /// transport surface; Kestrel's own default is unlimited, which is not a policy.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="MaxTotalConnections"/>: this one is enforced by the web server before our
    /// endpoint ever runs, and is the backstop if the endpoint's own accounting is ever wrong.
    /// </remarks>
    public int MaxConcurrentUpgradedConnections { get; set; } = 4096;

    /// <summary>
    /// Seconds a freshly accepted socket may stay silent before it must have sent <c>HelloCommand</c>.
    /// Zero or negative disables the deadline (test only).
    /// </summary>
    /// <remarks>
    /// Two seconds, not ten: before the handshake a socket holds a receive buffer, a connection slot and a
    /// per-IP pre-auth slot while having proved nothing. A real client sends its hello in the same round
    /// trip as the upgrade. The deadline is enforced by the connection supervisor's one-second sweep, so a
    /// stalled socket dies 2-3 s in — one sweep, not one timer per connection.
    /// </remarks>
    public int HandshakeTimeoutSeconds { get; set; } = 2;

    /// <summary>
    /// Largest frame accepted <b>before</b> authentication, and the size of the receive buffer an
    /// unauthenticated socket is allowed to hold. Exactly one such frame is accepted; a second one, or an
    /// oversized one, closes the connection.
    /// </summary>
    /// <remarks>
    /// A <c>HelloCommand</c> is a version, a token, a room id, a name and 16 bytes of resume key — well
    /// under 2 KiB even with a fat JWT. Sizing the pre-auth buffer at
    /// <see cref="QuotaOptions.MaxPayloadBytes"/> instead would let an unauthenticated flood pin the full
    /// per-connection buffer each.
    /// </remarks>
    public int MaxPreAuthFrameBytes { get; set; } = 2048;

    /// <summary>
    /// Sockets from one address that may be <i>unauthenticated</i> at the same time, on top of
    /// <see cref="QuotaOptions.MaxConnectionsPerIp"/>.
    /// </summary>
    /// <remarks>
    /// The general per-IP cap counts sockets that got as far as a room; this one bounds the cheap-to-open,
    /// expensive-to-hold pre-auth state, so one address cannot hold every connection slot on the server in
    /// the handshake phase. Combined with <see cref="HandshakeTimeoutSeconds"/> it bounds pre-auth
    /// occupancy per address to this many buffers for that many seconds.
    /// </remarks>
    public int MaxPreAuthConnectionsPerIp { get; set; } = 4;

    /// <summary>
    /// How many consecutive undecodable / unknown / illegal frames a connection may send before it is
    /// closed with <c>BadRequest</c>. A single bad frame is tolerated for forward compatibility — an
    /// unknown TypeId is how a newer client looks to an older server — but a stream of them is abuse. Zero
    /// or negative disables the cutoff (test only).
    /// </summary>
    public int MaxConsecutiveProtocolErrors { get; set; } = 16;

    /// <summary>
    /// Trust <c>X-Forwarded-For</c> for the client address (per-IP quotas and logging). Off by default:
    /// with no proxy in front, the header is attacker-controlled and would defeat the per-IP cap.
    /// Turn it on only when every request provably passes through your own proxy.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// Throws when a value would break an invariant the transport relies on. The composition root
    /// should call this right after binding so a bad appsettings fails startup, not the first client.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is outside its supported range.</exception>
    public void Validate()
    {
        if (InboundQueueCapacity < 1)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(InboundQueueCapacity)} must be at least 1.");
        }

        if (OutboundControlQueueCapacity < 1)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(OutboundControlQueueCapacity)} must be at least 1.");
        }

        if (OutboundHotQueueCapacity < 1)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(OutboundHotQueueCapacity)} must be at least 1.");
        }

        // A hot lane deeper than the control lane inverts the design: the shallow queue is the one whose
        // overflow is recoverable. Refuse rather than quietly serve stale deltas ahead of a rejection.
        if (OutboundHotQueueCapacity > OutboundControlQueueCapacity)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(OutboundHotQueueCapacity)} ({OutboundHotQueueCapacity}) must not exceed "
                + $"{nameof(OutboundControlQueueCapacity)} ({OutboundControlQueueCapacity}): a deep hot queue only "
                + "buffers staleness, while a full control queue closes the connection.");
        }

        if (MaxTotalConnections < 1)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(MaxTotalConnections)} must be at least 1.");
        }

        if (MaxConcurrentUpgradedConnections < 1)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(MaxConcurrentUpgradedConnections)} must be at least 1.");
        }

        if (MaxPreAuthFrameBytes < MinAllowedPreAuthFrameBytes || MaxPreAuthFrameBytes > MaxAllowedPreAuthFrameBytes)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxPreAuthFrameBytes)} must be between {MinAllowedPreAuthFrameBytes} and "
                + $"{MaxAllowedPreAuthFrameBytes}: below that a legitimate HelloCommand could not arrive, above it an "
                + "unauthenticated socket pins too much memory.");
        }

        if (MaxPreAuthConnectionsPerIp < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxPreAuthConnectionsPerIp)} must be at least 1; zero would refuse every "
                + "handshake, since every connection is unauthenticated for its first frame.");
        }
    }
}
