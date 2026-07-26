namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Per-connection and per-IP abuse limits, bound from configuration section <c>Rooms:Quotas</c>.
/// </summary>
/// <remarks>
/// Every limit is a hard ceiling, not a target: a well-behaved client at 20 Hz sits far below all of
/// them. Setting a limit to zero or a negative number disables it — useful for load tests, never in
/// production.
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
    public int MaxMessagesPerSecond { get; set; } = 120;

    /// <summary>
    /// Sustained inbound bytes per second per connection, with a one-second burst allowance. Breach
    /// closes with <c>RateLimited</c>.
    /// </summary>
    public int MaxBytesPerSecond { get; set; } = 262_144;

    /// <summary>
    /// Largest single frame accepted in either direction (spec default 16 KiB). Also the size of each
    /// connection's receive buffer, so a client cannot grow server memory by streaming continuation
    /// frames. Breach closes with <c>PayloadTooLarge</c>.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 16_384;

    /// <summary>
    /// Largest <c>Count</c> accepted in one <c>EntityUpdateFrame</c>. An over-sized frame is dropped
    /// and counted; the connection survives, because a mis-sized batch is a client bug, not an attack.
    /// </summary>
    public int MaxEntityUpdatesPerFrame { get; set; } = 64;

    /// <summary>Spawn requests one connection may make per minute. Excess requests are dropped and counted.</summary>
    public int MaxSpawnsPerMinute { get; set; } = 120;

    /// <summary>
    /// Seconds without a single inbound frame before a joined connection is closed with
    /// <c>IdleTimeout</c>. Clients keep this alive with <c>PingRequest</c>, which the protocol
    /// documents as proof of liveness.
    /// </summary>
    public int IdleTimeoutSeconds { get; set; } = 60;

    /// <summary>Chat messages one connection may send per minute. Excess messages are dropped and counted.</summary>
    public int MaxChatPerMinute { get; set; } = 30;

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

        if (MaxEntityUpdatesPerFrame is < 1 or > 255)
        {
            // The wire count field is a single byte.
            throw new InvalidOperationException($"{SectionName}:{nameof(MaxEntityUpdatesPerFrame)} must be between 1 and 255.");
        }
    }
}
