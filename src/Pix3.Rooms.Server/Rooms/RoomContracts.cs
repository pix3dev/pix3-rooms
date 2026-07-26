using System.Diagnostics.CodeAnalysis;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Net;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>How much authority the server exercises over entity state.</summary>
public enum RoomMode : byte
{
    /// <summary>
    /// Level 1: clients own their entities and the server validates ownership, quotas and AOI only.
    /// It never simulates.
    /// </summary>
    Relay = 0,

    /// <summary>
    /// Level 2+: the server owns the simulation and client input is advisory. Reserved; a Relay-only
    /// build must refuse to create an Authoritative room rather than silently degrade.
    /// </summary>
    Authoritative = 1,
}

/// <summary>Immutable creation parameters for one room. Never mutated after <c>TryCreate</c>.</summary>
public sealed record RoomConfig
{
    /// <summary>Room identity, unique per server. Also what room tokens are bound to.</summary>
    public required string RoomId { get; init; }

    /// <summary>Owning pix3 project; used for tenancy accounting and metrics labels.</summary>
    public required string ProjectId { get; init; }

    /// <summary>Optional build/version tag of the game that created the room.</summary>
    public string BuildId { get; init; } = "";

    /// <summary>Hard member cap; joins beyond it get <see cref="RejectCode.RoomFull"/>.</summary>
    public int MaxPlayers { get; init; } = 64;

    /// <summary>Room tick rate. Each room runs its own loop and its own budget at this rate.</summary>
    public int TickHz { get; init; } = 20;

    /// <summary>Area-of-interest radius in world units.</summary>
    public float AoiRadius { get; init; } = 1200f;

    /// <summary>Seconds a room may stay empty before the sweeper destroys it.</summary>
    public int IdleTtlSeconds { get; init; } = 300;

    /// <summary>Entity-table capacity; spawns beyond it get <see cref="RejectCode.EntityLimitReached"/>.</summary>
    public int MaxEntities { get; init; } = 4096;

    /// <summary>Authority model for this room.</summary>
    public RoomMode Mode { get; init; } = RoomMode.Relay;
}

/// <summary>
/// One decoded-but-not-deserialized inbound frame, handed from a socket to a room's inbound queue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Buffer layout.</b> <see cref="Payload"/> holds the <i>complete</i> frame:
/// <c>Payload[0] == TypeId</c> and <c>Payload[1..Length]</c> is the message payload.
/// <see cref="TypeId"/> is a convenience copy so dispatch never has to touch the array.
/// Hot-plane readers in <c>HotWire</c> expect the whole frame; MemoryPack control messages are
/// deserialized from <see cref="Body"/>.
/// </para>
/// <para>
/// <b>Ownership.</b> The array is rented from <see cref="FramePool"/>. Ownership transfers to the room
/// on a successful <c>TryEnqueueInbound</c>; the room returns it after handling. If the enqueue fails,
/// the caller returns it.
/// </para>
/// </remarks>
public readonly struct InboundMessage
{
    /// <summary>Sender, resolved from the connection — never read from the payload.</summary>
    public readonly uint ClientId;

    /// <summary>The frame's TypeId (same value as <c>Payload[0]</c>).</summary>
    public readonly byte TypeId;

    /// <summary>Rented buffer holding the complete frame; the room returns it to <see cref="FramePool"/> after handling.</summary>
    public readonly byte[] Payload;

    /// <summary>Valid byte count in <see cref="Payload"/>, including the leading TypeId.</summary>
    public readonly int Length;

    /// <summary>Wraps a rented frame buffer for hand-off to a room.</summary>
    /// <param name="clientId">Sender's client id.</param>
    /// <param name="typeId">Frame TypeId; must equal <c>payload[0]</c>.</param>
    /// <param name="payload">Rented buffer holding the complete frame.</param>
    /// <param name="length">Valid bytes in <paramref name="payload"/>, including the TypeId.</param>
    public InboundMessage(uint clientId, byte typeId, byte[] payload, int length)
    {
        ClientId = clientId;
        TypeId = typeId;
        Payload = payload;
        Length = length;
    }

    /// <summary>The complete frame bytes, TypeId included — what <c>HotWire</c> frame readers take.</summary>
    public ReadOnlySpan<byte> Frame => Length <= 0 ? default : Payload.AsSpan(0, Length);

    /// <summary>The payload after the TypeId — what MemoryPack deserialization takes.</summary>
    public ReadOnlySpan<byte> Body => Length <= 1 ? default : Payload.AsSpan(1, Length - 1);
}

/// <summary>
/// One room: its own state, its own tick loop, its own budget. A heavy room must never stall another,
/// and all room logic is single-threaded by contract — inbound work is queued and drained at tick start.
/// </summary>
public interface IRoom
{
    /// <summary>Creation parameters; immutable for the room's lifetime.</summary>
    RoomConfig Config { get; }

    /// <summary>Current member count.</summary>
    int PlayerCount { get; }

    /// <summary>When the room was created.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>Last time a member joined, left or sent something. Drives TTL eviction.</summary>
    DateTimeOffset LastActivityAt { get; }

    /// <summary>
    /// Admits a connection. On false, <paramref name="reject"/> says why (room full, closing, …) and
    /// the caller closes the socket with the mapped status.
    /// </summary>
    bool TryJoin(IClientConnection connection, out RejectCode reject);

    /// <summary>
    /// Removes a member, despawns everything it owned and fans out <see cref="PeerLeftEvent"/>.
    /// Idempotent: leaving an unknown client is a no-op.
    /// </summary>
    void Leave(uint clientId, LeaveReason reason);

    /// <summary>
    /// Non-blocking; false = room inbound queue full (caller drops the message, returns its buffer and
    /// counts the drop). Never blocks a socket thread on room work.
    /// </summary>
    bool TryEnqueueInbound(in InboundMessage message);

    /// <summary>
    /// Runs the room's tick loop until cancellation. A room that throws out of here must be destroyed
    /// and its clients closed with <see cref="RejectCode.InternalError"/>, never left half-alive.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>Point-in-time counters for admin and metrics endpoints. Safe to call from another thread.</summary>
    RoomStats SnapshotStats();
}

/// <summary>Point-in-time room counters.</summary>
/// <param name="PlayerCount">Members currently joined.</param>
/// <param name="EntityCount">Live entities in the replication table.</param>
/// <param name="ServerTick">Most recently completed tick.</param>
/// <param name="TickMsP50">Median tick duration in milliseconds.</param>
/// <param name="TickMsP99">99th-percentile tick duration in milliseconds.</param>
/// <param name="BytesOutPerSecond">Recent outbound throughput.</param>
/// <param name="DroppedFrames">Frames dropped because a send or inbound queue was full.</param>
/// <param name="BudgetOverruns">Ticks that exceeded their time budget.</param>
public sealed record RoomStats(int PlayerCount, int EntityCount, uint ServerTick,
                               double TickMsP50, double TickMsP99, long BytesOutPerSecond,
                               long DroppedFrames, long BudgetOverruns);

/// <summary>The room registry: lifecycle, lookup and enumeration. Thread-safe.</summary>
public interface IRoomManager
{
    /// <summary>Rooms currently alive.</summary>
    int RoomCount { get; }

    /// <summary>
    /// Creates and starts a room. False when the id is taken, the server room cap is reached or the
    /// config is invalid; <paramref name="reject"/> carries the wire-facing code and
    /// <paramref name="error"/> a human-readable detail for the admin API.
    /// </summary>
    bool TryCreate(RoomConfig config, [MaybeNullWhen(false)] out IRoom room, out RejectCode reject, out string? error);

    /// <summary>Looks up a live room by id.</summary>
    bool TryGet(string roomId, [MaybeNullWhen(false)] out IRoom room);

    /// <summary>
    /// Stops a room, closes its members with <see cref="RejectCode.RoomClosing"/> and removes it.
    /// False when no such room exists.
    /// </summary>
    bool Destroy(string roomId, string reason);

    /// <summary>Snapshot of every room's counters, for the admin and metrics endpoints.</summary>
    IReadOnlyList<RoomStats> ListStats();

    /// <summary>Snapshot of every room's configuration.</summary>
    IReadOnlyList<RoomConfig> ListConfigs();
}
