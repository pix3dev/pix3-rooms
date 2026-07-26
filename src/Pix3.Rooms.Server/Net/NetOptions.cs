namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Transport-level knobs, bound from configuration section <c>Rooms:Server</c>. A plain POCO on
/// purpose: the composition root binds and registers it, nothing here reaches into DI.
/// </summary>
/// <remarks>
/// The <c>Rooms:Server</c> section also carries <c>TickHz</c> and <c>MaxRooms</c>, which belong to the
/// Rooms module and are deliberately absent here — binding ignores unknown keys, so both modules can
/// bind the same section.
/// </remarks>
public sealed class NetOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Rooms:Server";

    /// <summary>
    /// Capacity of a room's inbound queue. Owned by the Rooms module (it creates the queue) but
    /// configured here because it is a transport back-pressure knob.
    /// </summary>
    public int InboundQueueCapacity { get; set; } = 1024;

    /// <summary>
    /// Capacity of a connection's outbound queue. When it is full the newest frame is dropped — see
    /// <see cref="ClientConnection.TryEnqueue"/> for why newest and not oldest.
    /// </summary>
    public int OutboundQueueCapacity { get; set; } = 256;

    /// <summary>
    /// Hard cap on simultaneously accepted WebSocket connections across all rooms. Checked before the
    /// upgrade is accepted; beyond it the endpoint answers 503.
    /// </summary>
    public int MaxTotalConnections { get; set; } = 4096;

    /// <summary>
    /// Seconds a freshly accepted socket may stay silent before it must have sent
    /// <c>HelloRequest</c>. Zero or negative disables the deadline (test only).
    /// </summary>
    public int HandshakeTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// How many consecutive undecodable / unknown / illegal frames a connection may send before it is
    /// closed with <c>BadRequest</c>. A single bad frame is tolerated for forward compatibility; a
    /// stream of them is abuse. Zero or negative disables the cutoff (test only).
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

        if (OutboundQueueCapacity < 1)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(OutboundQueueCapacity)} must be at least 1.");
        }

        if (MaxTotalConnections < 1)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(MaxTotalConnections)} must be at least 1.");
        }
    }
}
