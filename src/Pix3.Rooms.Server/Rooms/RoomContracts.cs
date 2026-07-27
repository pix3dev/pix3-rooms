using System.Diagnostics.CodeAnalysis;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Net;
using Pix3.Rooms.Server.Replication;

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

    /// <summary>Area-of-interest <i>enter</i> radius in world units (exit is <c>1.25 ×</c> this).</summary>
    public float AoiRadius { get; init; } = 1200f;

    /// <summary>Seconds a room may stay empty before the sweeper destroys it.</summary>
    public int IdleTtlSeconds { get; init; } = 300;

    /// <summary>
    /// Entity-table capacity; spawns beyond it get <see cref="RejectCode.EntityLimitReached"/>. Must be
    /// ≤ 65535: server→client records address entities by <c>u16 Slot</c>.
    /// </summary>
    public int MaxEntities { get; init; } = 4096;

    /// <summary>Authority model for this room.</summary>
    public RoomMode Mode { get; init; } = RoomMode.Relay;

    /// <summary>
    /// k-nearest visibility cap. Lives here, not only in <c>ReplicationOptions</c>, because
    /// <c>WelcomeEvent</c> must carry it and the handshake may read nothing but immutable room config
    /// across threads — and because it is the one AOI cap a game legitimately wants to tune per room. The
    /// room factory feeds <c>ReplicationOptions</c> from this value.
    /// </summary>
    public int MaxVisibleEntities { get; init; } = 64;

    /// <summary>
    /// World-space X of the low corner of this room's quantization range. Echoed in <c>WelcomeEvent</c>;
    /// changing it mid-room is impossible by construction (the room is recreated instead).
    /// </summary>
    public float WorldOriginX { get; init; } = -2048f;

    /// <summary>World-space Y of the low corner of this room's quantization range.</summary>
    public float WorldOriginY { get; init; } = -2048f;

    /// <summary>
    /// Side length of the square world every quantized value in this room is expressed against. Bounds
    /// must satisfy <see cref="WorldQuantizer.IsValidWorld"/>, which enforces the float32 precision ratio
    /// that keeps encode→decode→encode a fixed point.
    /// </summary>
    public float WorldSize { get; init; } = 4096f;

    /// <summary>
    /// Entity kinds this room accepts, indexing the build's prefab table. Empty = allow any (permitted in
    /// development only; production rooms must pass an explicit list). A kind outside the list is refused
    /// with <see cref="RejectCode.KindNotAllowed"/>, because an unknown kind would fault every observer's
    /// scene code.
    /// </summary>
    public IReadOnlyList<ushort> AllowedKinds { get; init; } = [];
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
/// Everything the handshake needs to build a <c>WelcomeEvent</c>, handed back by the room because the
/// room — not the transport — owns membership, host promotion, resume keys and the tick counter.
/// </summary>
/// <param name="ClientId">
/// The id this session runs under. On a fresh join it echoes the id the transport allocated; on a resume
/// it is the dropped session's <b>original</b> id, which the transport adopts before publishing the member.
/// </param>
/// <param name="HostClientId">The room's host at grant time, or 0 when the room has none.</param>
/// <param name="ServerTick">The room's most recently published tick.</param>
/// <param name="ResumeKey">A fresh 16-byte resume credential, regenerated on every connect.</param>
/// <param name="Resumed">True when this grant answered a successful resume rather than a fresh join.</param>
/// <remarks>A record struct, so a join allocates only its key.</remarks>
public readonly record struct JoinGrant(
    uint ClientId, uint HostClientId, uint ServerTick, byte[] ResumeKey, bool Resumed);

/// <summary>
/// One room: its own state, its own tick loop, its own budget. A heavy room must never stall another,
/// and all room logic is single-threaded by contract — inbound work is queued and drained at tick start.
/// </summary>
public interface IRoom
{
    /// <summary>Creation parameters; immutable for the room's lifetime.</summary>
    RoomConfig Config { get; }

    /// <summary>
    /// Current member count, including sessions inside their resume grace — peers were never told those
    /// left, and their slots are still reserved, so they still count against <see cref="RoomConfig.MaxPlayers"/>.
    /// </summary>
    int PlayerCount { get; }

    /// <summary>
    /// Longest-present member, or 0 when the room is empty. Announced with <c>HostChangedEvent</c> on
    /// change. A volatile read, safe from a socket thread.
    /// </summary>
    uint HostClientId { get; }

    /// <summary>
    /// The tick published by the room's own thread. A volatile read, cheap enough for a socket thread to
    /// stamp a <c>PongEvent</c> with — <see cref="SnapshotStats"/> is not, since it allocates a record and
    /// samples histograms.
    /// </summary>
    uint ServerTick { get; }

    /// <summary>When the room was created.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>Last time a member joined, left or sent something. Drives TTL eviction.</summary>
    DateTimeOffset LastActivityAt { get; }

    /// <summary>
    /// Admits a connection. On false, <paramref name="reject"/> says why (room full, closing, …) and the
    /// caller closes the socket with the mapped status. On true <paramref name="grant"/> carries
    /// everything the <c>WelcomeEvent</c> needs.
    /// </summary>
    bool TryJoin(IClientConnection connection, out JoinGrant grant, out RejectCode reject);

    /// <summary>
    /// Re-attaches a session that dropped inside its resume grace. The 16-byte key is the only
    /// credential — a client never claims an id, so a leaked id cannot be impersonated. False with
    /// <see cref="RejectCode.None"/> = no such pending session, and the caller falls back to
    /// <see cref="TryJoin"/> (a failed resume is a fresh join, never an error path). The grant carries the
    /// ORIGINAL client id, which the transport adopts before publishing the member. A non-None reject
    /// means a REAL refusal (the room is closing or full) and the transport surfaces it instead of
    /// retrying as a join — the fallback applies only to "this key names no pending session".
    /// </summary>
    bool TryResume(IClientConnection connection, ReadOnlySpan<byte> resumeKey,
                   out JoinGrant grant, out RejectCode reject);

    /// <summary>
    /// Removes a member. A <see cref="LeaveReason.Disconnected"/> socket teardown starts the resume
    /// grace instead: the member's entities stay alive and frozen and <c>PeerLeftEvent</c> is deferred.
    /// Every other reason (voluntary leave, kick, timeout, room closed, error) leaves for real at once.
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

    /// <summary>
    /// Per-client violation tallies (ownership, speed, mask, nan, kind, quota, focusClamp, teleport).
    /// Build the dataset now, the detector later.
    /// </summary>
    ViolationCounters SnapshotViolations(uint clientId);
}

/// <summary>Point-in-time room counters.</summary>
/// <param name="PlayerCount">Members currently joined, resume-grace sessions included.</param>
/// <param name="EntityCount">Live entities in the replication table.</param>
/// <param name="ServerTick">Most recently completed tick.</param>
/// <param name="TickMsP50">Median tick body duration in milliseconds.</param>
/// <param name="TickMsP99">99th-percentile tick body duration in milliseconds.</param>
/// <param name="TickJitterMsP99">
/// 99th-percentile lateness of a tick's start against its absolute deadline. This is the number that
/// proves the scheduler is working; tick <i>body</i> time can be perfect while starts jitter by 15 ms.
/// </param>
/// <param name="BytesOutPerSecond">Recent outbound throughput.</param>
/// <param name="DroppedFrames">Frames dropped because a send or inbound queue was full.</param>
/// <param name="BudgetOverruns">Ticks whose body exceeded the tick interval.</param>
/// <param name="Resyncs">Known-set rebuilds this room asked for (hot-lane overflow, <c>ResyncCommand</c>).</param>
/// <param name="Violations">Sum of every member's violation counters at the last publish.</param>
public sealed record RoomStats(int PlayerCount, int EntityCount, uint ServerTick,
                               double TickMsP50, double TickMsP99, double TickJitterMsP99,
                               long BytesOutPerSecond, long DroppedFrames, long BudgetOverruns,
                               long Resyncs, long Violations);

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
