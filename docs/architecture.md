# Pix3 Rooms — architecture and module contract

`pix3-rooms` is the **Room Fabric** for pix3: a multi-tenant .NET WebSocket server that hosts game rooms for scenes authored in the pix3 editor. It terminates sockets, validates identity, owns room lifecycle and quotas, and replicates generic entity state with area-of-interest filtering. **It never learns pix3 scene semantics** (nodes, components, prefabs) — those stay in `@pix3/runtime` on the client, and later in Level-3 server workers behind this gateway.

Full product context and phasing: `pix3/.plans/multiplayer-platform.md`. Wire contract: [`protocol.md`](./protocol.md) (**v2** — the first version that ships).

## Non-negotiable design constraints

The flagship requirement is **600 concurrent players in one room, 2D top-down shooter, without lag**. That dictates the hot path:

1. **AOI is core, not an optimization.** Broadcast-all at 600 players is ~4.3 Gbit/s — dead on arrival. A uniform spatial hash restricts each client to the ~30–50 entities near it, with a hard `MaxVisibleEntities` k-nearest cap on top because a radius does not bound the dogpile case.
2. **Encode-once, memcpy-many.** Per tick, each dirty entity's `UpdateRecord` bytes are written **once** into a scratch buffer; per-client packets are assembled by copying those byte ranges. Never re-serialize per recipient. The same discipline covers AOI signal batches.
3. **Zero allocation on the tick path.** Structure-of-arrays state, pre-allocated buffers, `ArrayPool<byte>` for frames, bitsets for AOI membership. No LINQ, no `foreach` over interfaces, no lambda captures, no `List<T>` growth inside a tick.
4. **Room = isolated unit.** Each room owns its state, its tick loop and its budget. One heavy room must never stall another (the WsCore reference server has a single global loop — we deliberately do not).
5. **Single-threaded room logic.** Sockets hand inbound messages to a per-room queue; the room drains it at tick start. Room state is touched by exactly one thread at a time, so no locks in game logic.
6. **The lossy link is our own send queue, not the network.** WSS is TCP, so nothing on the wire is lost — but a bounded per-connection queue drops frames under back-pressure. Enter and removal records do not self-heal, so **a known-set bit may only be flipped after the frame carrying it was accepted for sending** (two-phase commit), with `Seq` + `ResyncCommand` as the safety net. This is a correctness constraint, not a robustness nicety.
7. **Quantized integers are the replicated values**, on both sides, including for dirty detection. Positions are `u16`, rotation `u8`, velocity `i16` against per-room world bounds. Comparing floats for dirtiness would keep an idle entity dirty forever on sub-quantum noise.
8. **Nothing game-specific.** No players/HP/bullets/weapons in this repo. Entities are `(netId, owner, kind, transform, flags, cold props)` — and projectiles are not entities at all: firing is an AOI-scoped signal that every client simulates locally.

## Reference implementation to learn from

`WsCore` (the predecessor experiment, path supplied per task) has a battle-tested socket layer worth porting in spirit:

- `WsServer/WsServer/WebSocketHandler.cs` — frame reassembly to `EndOfMessage`, a bounded per-connection `Channel` + a single send loop, consecutive-error cutoff, message-size cap.
- `Shared/MessageSerializer.cs` — the `[TypeId][payload]` framing.
- `Shared/ReflectionServerLogicProvider.cs` — reflection handler discovery with boot-time duplicate-TypeId detection.

Things it got wrong that we must **not** copy: one global `GameModel` shared by all rooms; global broadcast ignoring room membership; no AOI (its docs claim otherwise); no auth; a JSON text-frame fallback that reflects properties while DTOs use fields; `[Flags]` enum with values 0/1/2/3; `CreateRoom` ignoring `TryAdd`. Also **not** its `DropOldest` queue policy: it silently discards a frame whose known-set bit was already flipped, and it leaks the rented buffer unless `itemDropped` is wired. We use lanes (below).

## Module map and ownership

One folder = one owner. Do not create or edit files outside your folder; if you need something from another module, use the seam below exactly as declared.

| Folder | Namespace | Responsibility |
|---|---|---|
| `src/Pix3.Rooms.Protocol/` | `Pix3.Rooms.Protocol` | Wire contract: MemoryPack messages, TypeId map, hand-packed hot codecs, quantizer, version consts, reject codes |
| `src/Pix3.Rooms.Server/Net/` | `Pix3.Rooms.Server.Net` | Kestrel WS endpoint, connection objects, send-queue lanes, inbound decode + dispatch, handshake + pre-auth gate, quotas/rate limits |
| `src/Pix3.Rooms.Server/Auth/` | `Pix3.Rooms.Server.Auth` | Room-token (JWT) validation, service-token validation, origin policy, dev/insecure mode |
| `src/Pix3.Rooms.Server/Rooms/` | `Pix3.Rooms.Server.Rooms` | Room, room manager/registry, per-room tick thread, TTL eviction, membership, resume grace, host migration, room-scoped fan-out, chat, room vars |
| `src/Pix3.Rooms.Server/Replication/` | `Pix3.Rooms.Server.Replication` | Entity table (SoA, quantized), spatial hash AOI, per-subscriber known-sets + `Seq`, encode-once delta/snapshot/signal-batch assembly |
| `src/Pix3.Rooms.Server/Admin/` | `Pix3.Rooms.Server.Admin` | Admin REST API for room lifecycle (service-token auth), violation counters, `/health` |
| `src/Pix3.Rooms.Server/Observability/` | `Pix3.Rooms.Server.Observability` | Dependency-free metrics registry + Prometheus text endpoint |
| `src/Pix3.Rooms.Server/Program.cs`, `RoomsFabricExtensions.cs`, `MetricsBridge.cs`, `appsettings*.json` | `Pix3.Rooms.Server` | Composition root: options binding + validation, DI, endpoint wiring, metric pass-through |
| `tests/Pix3.Rooms.Tests/` | `Pix3.Rooms.Tests` | xUnit: golden wire vectors, AOI, recovery, room lifecycle, quotas, auth |
| `tools/Pix3.Rooms.LoadGen/` | `Pix3.Rooms.LoadGen` | Headless load generator: N bot clients against a room, latency/bandwidth/jitter report |

Dependencies flow one way: `Protocol` ← `{Net, Rooms, Replication, Auth, Admin}`; `Net` → `Rooms` (enqueue) and `Auth`; `Rooms` → `Replication` and `Net` (send); `Admin` → `Rooms`. `Replication` depends on `Protocol` only — it must be unit-testable with no sockets. `Net` must not depend on `Observability`: its counters are a plain `NetMetrics` surface that the composition root's `MetricsBridge` pumps into the Prometheus registry.

## Cross-module seams (implement these signatures verbatim)

```csharp
// ── Net ────────────────────────────────────────────────────────────────────
namespace Pix3.Rooms.Server.Net;

/// Which send queue a frame belongs to. The two lanes have different failure policies, and that
/// difference is a correctness contract, not tuning.
public enum FrameLane : byte
{
    /// Handshake, chat, room vars, spawn responses, signals, rejections. A full control lane means the
    /// client is unrecoverably behind: close the connection. Control frames are never dropped silently.
    Control = 0,
    /// Snapshots, deltas, signal batches. A full hot lane returns the buffer and marks the client for
    /// resync; the known-set changes that frame carried are rolled back.
    Hot = 1,
}

/// A live socket. Room logic only ever sees this interface.
public interface IClientConnection
{
    /// Room-unique, monotonic per server. Allocated by Net only AFTER the handshake authenticates — an
    /// unauthenticated socket consumes no id and no room state — and replaced by the resumed session's
    /// original id when a resume succeeds (Net owns the allocator; the room hands the old id back in a
    /// JoinGrant, and the transport adopts it before the member is published).
    uint   ClientId    { get; }
    string RemoteIp    { get; }
    string DisplayName { get; }
    bool   IsOpen      { get; }
    /// Enqueue an already-encoded frame on a lane. False = queue full or closed (caller must return the
    /// buffer). Ownership transfers on success only.
    bool TryEnqueue(in OutboundFrame frame, FrameLane lane);
    /// Send RejectedEvent (when code != None) and close with the mapped WS status.
    void RequestClose(RejectCode code, string reason);
}

/// A rented buffer holding one complete frame ([TypeId][payload]). Ownership transfers on TryEnqueue
/// success; the send loop returns it to the pool. Broadcast copies per recipient (no refcounting).
public readonly struct OutboundFrame
{
    public readonly byte[] Buffer;
    public readonly int Length;
    public OutboundFrame(byte[] buffer, int length);
}

/// Pool + encode helpers shared by Net and Rooms.
public static class FramePool
{
    public static byte[] Rent(int minimumLength);
    public static void Return(byte[] buffer);
    /// MemoryPack-encode a control message into a rented buffer, prefixed with its TypeId.
    public static OutboundFrame EncodeControl<T>(byte typeId, T message);
}

// ── Rooms ──────────────────────────────────────────────────────────────────
namespace Pix3.Rooms.Server.Rooms;

public enum RoomMode : byte { Relay = 0, Authoritative = 1 }   // Relay = Level 1 (client authority)

public sealed record RoomConfig
{
    public required string RoomId     { get; init; }
    public required string ProjectId  { get; init; }
    public string  BuildId            { get; init; } = "";
    public int     MaxPlayers         { get; init; } = 64;
    public int     TickHz             { get; init; } = 20;
    public float   AoiRadius          { get; init; } = 1200f;
    public int     IdleTtlSeconds     { get; init; } = 300;
    public int     MaxEntities        { get; init; } = 4096;   // must be <= 65535 (slot addressing)
    public RoomMode Mode              { get; init; } = RoomMode.Relay;
    /// k-nearest visibility cap. Lives here, not only in ReplicationOptions, because WelcomeEvent must
    /// carry it and the handshake may read nothing but immutable room config across threads — and because
    /// it is the one AOI cap a game legitimately wants to tune per room. The room factory feeds
    /// ReplicationOptions from this value.
    public int     MaxVisibleEntities { get; init; } = 64;
    // World bounds every quantized value in this room is expressed against. Echoed in WelcomeEvent;
    // changing them mid-room is impossible by construction (the room is recreated instead).
    public float   WorldOriginX       { get; init; } = -2048f;
    public float   WorldOriginY       { get; init; } = -2048f;
    public float   WorldSize          { get; init; } = 4096f;
    /// Entity kinds this room accepts, indexes into the build's prefab table. Empty = allow any
    /// (permitted in development only; production rooms must pass an explicit list).
    public IReadOnlyList<ushort> AllowedKinds { get; init; } = [];
}

public readonly struct InboundMessage
{
    public readonly uint ClientId;
    public readonly byte TypeId;
    public readonly byte[] Payload;   // rented; the room returns it to FramePool after handling
    public readonly int Length;
}

/// Everything the handshake needs to build a WelcomeEvent, handed back by the room because the room —
/// not the transport — owns membership, host promotion, resume keys and the tick counter. A record
/// struct so a join allocates only its key.
public readonly record struct JoinGrant(
    uint ClientId, uint HostClientId, uint ServerTick, byte[] ResumeKey, bool Resumed);

public interface IRoom
{
    RoomConfig Config      { get; }
    int        PlayerCount { get; }
    /// Longest-present member, or 0 when the room is empty. Announced with HostChangedEvent on change.
    uint       HostClientId { get; }
    /// The tick published by the room's own thread. A volatile read, cheap enough for a socket thread to
    /// stamp a PongEvent with — SnapshotStats() is not, since it allocates a record and samples histograms.
    uint       ServerTick  { get; }
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset LastActivityAt { get; }
    bool TryJoin(IClientConnection connection, out JoinGrant grant, out RejectCode reject);
    /// Re-attaches a session that dropped inside its resume grace. The 16-byte key is the only
    /// credential — a client never claims an id, so a leaked id cannot be impersonated. False with
    /// RejectCode.None = no such pending session, and the caller falls back to TryJoin (a failed resume
    /// is a fresh join, never an error path). The grant carries the ORIGINAL client id, which the
    /// transport adopts before publishing the member. A non-None reject means a REAL refusal (the room is
    /// closing or full) and the transport surfaces it instead of retrying as a join — the fallback applies
    /// only to "this key names no pending session".
    bool TryResume(IClientConnection connection, ReadOnlySpan<byte> resumeKey,
                   out JoinGrant grant, out RejectCode reject);
    void Leave(uint clientId, LeaveReason reason);
    /// Non-blocking; false = room inbound queue full (caller drops + counts).
    bool TryEnqueueInbound(in InboundMessage message);
    Task RunAsync(CancellationToken cancellationToken);
    RoomStats SnapshotStats();
    /// Per-client violation tallies (ownership, speed, mask, nan, kind, quota, focusClamp, teleport).
    /// Build the dataset now, the detector later.
    ViolationCounters SnapshotViolations(uint clientId);
}

public sealed record RoomStats(int PlayerCount, int EntityCount, uint ServerTick,
                               double TickMsP50, double TickMsP99, double TickJitterMsP99,
                               long BytesOutPerSecond, long DroppedFrames, long BudgetOverruns,
                               long Resyncs, long Violations);

public interface IRoomManager
{
    int RoomCount { get; }
    bool TryCreate(RoomConfig config, out IRoom room, out RejectCode reject, out string? error);
    bool TryGet(string roomId, out IRoom room);
    bool Destroy(string roomId, string reason);
    IReadOnlyList<RoomStats> ListStats();
    IReadOnlyList<RoomConfig> ListConfigs();
}

// ── Replication ────────────────────────────────────────────────────────────
namespace Pix3.Rooms.Server.Replication;

/// What a Write* call intends to change in a client's known set, plus the Seq it stamped. Opaque to
/// callers: hand it back to Commit (frame enqueued) or Rollback (enqueue failed). Nothing else may
/// mutate a known set.
public readonly struct PendingKnownSetCommit
{
    public readonly uint ClientId;
    public readonly ushort Seq;
    public readonly bool IsFinalSnapshotFrame;
    /// Pairs the handle with the subscriber's current pending frame, so a duplicate or stale commit is
    /// detected rather than silently corrupting a known set. Opaque; 0 means "no frame was produced".
    public readonly uint Token;
    public bool IsEmpty { get; }
}

/// Per-client tallies of everything a client did that the fabric refused. Lives here rather than in
/// Rooms because Replication produces most of them and the dependency arrow only points this way;
/// Rooms merges its own quota and chat numbers in before exposing the record through IRoom.
/// Build the dataset now, the detector later.
public readonly record struct ViolationCounters(
    long Ownership, long Speed, long Mask, long Nan,
    long Kind, long Quota, long FocusClamp, long Teleport);

/// Owns entity state, AOI, per-client Seq and all hot-path encoding for ONE room. Single-threaded by
/// contract.
public interface IRoomReplication
{
    int EntityCount { get; }

    bool TrySpawn(uint ownerId, ushort kind, in EntityWireState state, out uint netId, out RejectCode reject);
    bool TryDespawn(uint netId, uint requesterId, out RejectCode reject);
    /// Applies one client update record; false when not owned / unknown / stale generation / illegal mask
    /// / out-of-range quantized field. Dirty detection compares quantized integers.
    bool TryApplyOwnedUpdate(uint netId, uint ownerId, byte mask, in EntityWireState state);
    /// Despawns the leaving owner's `Owned` entities; appends removed ids to `despawned`.
    /// `Shared`/`Transferable` entities are left alone for ReassignOwner.
    void RemoveOwner(uint ownerId, List<uint> despawned);
    /// Host migration: moves `Shared`/`Transferable` entities from one owner to another (usually the
    /// promoted host); appends the moved ids to `reassigned`.
    void ReassignOwner(uint fromOwnerId, uint toOwnerId, List<uint> reassigned);

    void AddSubscriber(uint clientId);
    void RemoveSubscriber(uint clientId);
    /// Binds this client's AOI centre to an owned entity's SERVER-SIDE position, refreshed every tick.
    /// This is the normal path: it deletes focus-teleport amplification at its source.
    void BindSubscriberFocus(uint clientId, uint netId);
    /// Free-coordinate focus for spectators only. Movement is speed-clamped; a clamp increments the
    /// client's focusClamp counter.
    void SetSpectatorFocus(uint clientId, float x, float y);
    /// Clears this client's known set and restarts its snapshot cursor: the next tick emits a full
    /// snapshot. Covers queue overflow, tab refocus and reconnect with one primitive.
    void RequestResync(uint clientId);
    /// Hidden clients get no hot frames at all and their Seq stops advancing; un-hiding implies a resync.
    void SetSubscriberHidden(uint clientId, bool hidden);
    /// 1 = every tick, n = every nth tick. Clamped to [1, 8]. Control frames are unaffected.
    void SetSubscriberSendDivisor(uint clientId, byte divisor);

    /// Queues one AOI-scoped signal for this tick, encoded once here and copied per recipient by
    /// WriteSignalBatch. False = the signal is too large for the hot plane or the tick's batch is full.
    bool TryQueueAoiSignal(uint senderClientId, ReadOnlySpan<byte> name, ReadOnlySpan<byte> payload);

    /// Rebuild grid, recompute visibility (k-nearest + hysteresis), fill encode-once scratch, refresh
    /// bound focuses. Call once per tick, before any Write*.
    void Tick(uint serverTick);
    /// Writes one complete SnapshotPacket (TypeId included). Returns bytes written, 0 if none. The
    /// continuation cursor is per-subscriber state, so a resync restarts it; the frame carrying the last
    /// records reports IsFinalSnapshotFrame.
    int WriteSnapshot(uint clientId, Span<byte> destination, out PendingKnownSetCommit commit);
    /// Writes one complete DeltaPacket (TypeId included), bounded by the destination length and the
    /// per-tick byte budget. Returns 0 when this client has nothing to receive.
    int WriteDelta(uint clientId, Span<byte> destination, out PendingKnownSetCommit commit);
    /// Writes this client's SignalBatchPacket for the current tick. 0 when it has no signals.
    int WriteSignalBatch(uint clientId, Span<byte> destination, out PendingKnownSetCommit commit);
    /// The frame was accepted by the send queue: apply the known-set changes and advance Seq.
    void Commit(in PendingKnownSetCommit commit);
    /// The frame was NOT sent: discard the intended changes and leave Seq untouched, so the client never
    /// sees a gap for a frame that never existed.
    void Rollback(in PendingKnownSetCommit commit);

    /// This client's violation tallies. Rooms merges its own numbers into the result.
    ViolationCounters SnapshotViolations(uint clientId);

    /// True while this client still owes snapshot frames. The snapshot cursor is core state, so the room
    /// asks instead of keeping a copy that could drift.
    bool IsSnapshotPending(uint clientId);
    /// Marks cold props dirty, so the next update carries ColdDirty and the client expects the event.
    bool TryMarkColdDirty(uint netId);
    /// Records a spawn refused by the room's entity-kind allowlist. The policy is room data; the tally
    /// belongs with the other per-client violation counters.
    void CountKindViolation(uint clientId);
}

// ── Auth ───────────────────────────────────────────────────────────────────
namespace Pix3.Rooms.Server.Auth;

public sealed record RoomTokenClaims(string Subject, string RoomId, string Role,
                                     bool IsGuest, string? DisplayName, DateTimeOffset ExpiresAt);

public interface IRoomTokenValidator
{
    bool TryValidate(string token, string requestedRoomId, out RoomTokenClaims claims, out RejectCode reject);
}

public interface IServiceTokenValidator
{
    bool IsValid(string? presentedToken);   // constant-time comparison
}

/// Cross-site WebSocket hijacking defence: the upgrade's Origin must be on the allowlist. An empty
/// allowlist accepts any origin and is permitted in development only.
public interface IOriginPolicy
{
    bool IsAllowed(string? origin);
}
```

`Seq` is stamped from the *peek* value when a frame is written and advanced by `Commit`. A rolled-back frame therefore leaves `Seq` untouched and the client never learns the frame existed — which is exactly right, because its known-set changes were rolled back too.

## Runtime topology

```
Kestrel  ──/ws?room=<id>──►  WebSocketEndpoint
                                │  origin allowlist, pre-auth bucket, IP cap, 2 s handshake deadline,
                                │  one ≤2 KiB frame accepted before auth, token validate, room lookup,
                                │  TryResume → else TryJoin
                                ▼
                            IRoom  ──► per-room inbound Channel (bounded)
                                                     │
                dedicated per-room thread, absolute deadlines (t0 + n·freq/tickHz), missed ticks skipped
                       drain inbound ─► apply to IRoomReplication ─► Tick(serverTick)
                       ─► per client: WriteSnapshot | WriteDelta | WriteSignalBatch
                                       ─► TryEnqueue(Hot) ─► Commit  ·  else Rollback + RequestResync
                                                     │
                        per-connection Control lane + Hot lane ─► send loop ─► socket
```

Admin REST (`/admin/rooms`, service token) creates and destroys rooms; a sweeper destroys rooms that have been empty longer than `IdleTtlSeconds` (after a creation grace, so a freshly created room is not swept before its first player arrives).

## Implementation decisions

These were settled by a best-practice review (Valve/Source, Quake 3, Fiedler, Overwatch's GDC netcode talk, Tribes/Halo:Reach interest management, Colyseus, Unity NGO/Mirror/FishNet/Netick, Godot, Photon, Rune, SpacetimeDB, plus browser-transport and .NET 10 specifics). They are decided, not open.

**Tick loop.** A dedicated thread per room with **absolute deadlines** (`t0 + n × Stopwatch.Frequency / tickHz`), sleeping to a margin short of the deadline and spinning the tail, **skipping missed ticks rather than catching up**. `PeriodicTimer` silently coalesces missed ticks, which is why it is not used.

**The spin margin must exceed the platform's timer slice**, not merely be smaller than the tick. Windows' default slice is 15.625 ms, so a sleep aimed 2 ms short of the deadline routinely wakes up *past* it and the tail never gets to absorb anything. Measured on one Windows box with an otherwise-empty 20 Hz loop: a 2 ms margin gives **p50 6.7 ms late**, p99 24.7 ms — every tick late, systematically — while a 17 ms margin gives **p50 0.001 ms**, p99 11 ms (the p99 being that loaded box's own scheduling noise; an ideal loop measured 9.5 ms on it at the same moment). So the margin is platform-dependent: ~17 ms where the timer is coarse, 2 ms where a sleep is already ~1 ms accurate.

The spin tail is **conditional** — coarse-granularity platform, or a room above a player threshold, and in both cases only while the room actually has members. 64 idle rooms each burning a core fraction to busy-wait is a bad trade, nobody can feel an empty room's jitter, and production is Linux, where plain sleeping is ~1 ms accurate anyway. Tick **start jitter** is a first-class metric (`RoomStats.TickJitterMsP99`) precisely because it is what proves the loop works; the tick-body histogram cannot see it.

**GC.** Server GC, concurrent, with **DATAS explicitly off** (`GarbageCollectionAdaptationMode = 0`) — it is enabled by default with Server GC since .NET 9, ramps heap count reactively and schedules full compacting collections, with 1.16–1.69× regressions reported on latency-sensitive workloads. Already applied in `Directory.Build.props`; do not remove it.

**Transport hardening.** Never enable `permessage-deflate` (64–316 KiB of zlib context per connection, and context takeover breaks datagram portability) — with a handshake test asserting no `Sec-WebSocket-Extensions` in the 101. Pin `/ws` to HTTP/1.1 (browsers negotiate RFC 8441 WebSockets-over-HTTP/2 by default — pure overhead for one long-lived binary socket). `KeepAliveInterval`/`KeepAliveTimeout` at 15 s so protocol-level pings survive throttled tabs and dead mobile sockets are detected. Set `MaxConcurrentUpgradedConnections` explicitly; Kestrel's default is unlimited.

**Pre-auth gate.** A 2 s handshake timeout, exactly **one** frame of ≤2 KiB accepted before authentication, no client id or room state allocated until then, an Origin allowlist, a new-connection token bucket per IP, and a **per-IP** pre-auth connection cap (`MaxPreAuthConnectionsPerIp`; the process-wide ceiling is already `MaxTotalConnections`). The token stays in the first frame — **never** in the query string (query strings land in access logs and referrers). The handshake deadline is enforced by the supervisor's 1 s sweep rather than 600 timers, so the effective kill window is the deadline plus one sweep.

**Serialization.** All control messages are version-tolerant with explicit member ordering **from v2 onwards**, because retrofitting that is itself a wire break. **Do not use MemoryPack's TypeScript generator** — it has an open nullable-float correctness bug; hand-write the client control codecs and gate CI on golden vectors produced by the C# side.

**Verification gates.** A zero-allocation CI test asserting `GC.GetAllocatedBytesForCurrentThread()` is unchanged across 10 000 simulated ticks; a debug-only generation-stamped buffer pool that fills returned buffers with `0xDD` and asserts on use-after-return; and three histograms — **tick start jitter**, **tick body time**, and **enqueue-to-socket-write** (the last being the one players actually feel).

**Deferred, not rejected:** single-use `jti` cache and ES256 + JWKS (do them when identity becomes a separate deployable), per-room deny-sets, per-room tuning of the AOI caps.

**Rejected for good** (do not re-propose without new evidence): ack bitfields and delta-from-acknowledged baselines; replicated projectile entities; per-kind hot schemas; Fiedler's priority accumulator (the k-nearest cap plus a byte budget covers the dogpile case — revisit only if load tests show the cap biting in normal play); bit-packing, smallest-three quaternions, varints, range coding, any stream compression; Colyseus schema / FlatBuffers / Cap'n Proto / Bebop on the hot plane; Orleans / MagicOnion / Nakama / SpacetimeDB / Rune / Croquet as the fabric; WebRTC DataChannel; input prediction, reconciliation, time dilation and server rewind (Level-1 owners simulate locally with zero latency, and there is no server hit detection to rewind — these are Level-2/3 items); stripping the client teleport bit at Level 1; NativeAOT, ReadyToRun, `System.IO.Pipelines`, GC heap affinity, `GCHeapHardLimitPercent`, `timeBeginPeriod(1)`, per-room slab allocators; HTTP/2 WebSockets.

## Configuration (appsettings, section `Rooms`)

Every key below is read by code. The composition root binds, validates and fails startup on anything unusable — a bad file must never reach the first client. Environment overrides use the standard double-underscore form (`Rooms__Auth__JwtSecret`).

**`Rooms:Server`** — server-wide rails. Bound by both `NetOptions` (transport keys) and `RoomServerOptions` (room keys); configuration binding ignores keys a type does not declare, which is why one section serves both.

| Key | Default | Owner |
|---|---|---|
| `MaxRooms` | 256 | Rooms |
| `MaxTotalConnections` | 4096 | Net |
| `MaxConcurrentUpgradedConnections` | 4096 | Net declares and validates it; the composition root applies it to Kestrel |
| `InboundQueueCapacity` | 4096 | both — **write it explicitly**, the two types' built-in defaults differ |
| `OutboundControlQueueCapacity` | 64 | Net |
| `OutboundHotQueueCapacity` | 8 | Net — a deep hot queue only buffers staleness; overflow means resync |
| `HandshakeTimeoutSeconds` | 2 | Net |
| `MaxPreAuthFrameBytes` | 2048 | Net |
| `MaxPreAuthConnectionsPerIp` | 4 | Net |
| `MaxConsecutiveProtocolErrors` | 16 | Net |
| `TrustForwardedHeaders` | false | Net — on only when every request provably passes your own proxy |
| `MaxDrainPerTick` | 2048 | Rooms |
| `MaxFrameBytes` | 16384 | Rooms — control-frame ceiling |
| `MaxBytesPerClientPerTick` | 1100 | Replication — one MSS / one future datagram |
| `MaxEntersPerTick` | 24 | Replication |
| `MaxVisibleEntities` | 64 | Replication — k-nearest by squared distance, applied to the exit-radius set |
| `AoiExitFactor` | 1.25 | Replication — hysteresis |
| `MaxEntitySpeed` | 2000 | Replication — units/s for the counted-only Level-1 speed check (125 units per tick at 20 Hz) |
| `MaxSpectatorFocusSpeed` | 2000 | Replication — units/s cap on free-coordinate focus movement |
| `MaxSnapshotFramesPerTick` | 8 | Rooms |
| `MaxConsecutiveTickFailures` | 5 | Rooms |
| `IdleSweepIntervalSeconds` | 15 | Rooms |
| `RoomCreationGraceSeconds` | 30 | Rooms |
| `ResumeGraceSeconds` | 30 | Rooms — 0 disables the grace, and a drop then leaves with `Disconnected` |
| `SpinTailPlayerThreshold` | 32 | Rooms — the tick loop spins its tail on a coarse-granularity platform, or above this many players; 0 always spins |
| `MaxColdPropsPerSecond` | 2 | Rooms — **per entity**; the `Quotas` twin of this name is per connection |
| `MaxChatPerMinute` | 10 | Rooms — room-level; the connection-level twin lives in `Quotas` |
| `MaxChatLength` | 240 | Rooms |
| `RestrictRoomVarsToHost` | true | Rooms |
| `MaxRoomVars` | 64 | Rooms |
| `MaxRoomVarKeyLength` | 64 | Rooms |
| `MaxRoomVarValueBytes` | 4096 | Rooms |
| `MaxColdPropsBytes` | 512 | Rooms |
| `MaxEntitiesPerOwner` | 64 | Rooms |
| `MaxSignalNameLength` | 64 | Rooms |
| `MaxSignalPayloadBytes` | 255 | Rooms — the hot batch encodes the length in one byte |
| `TickHistogramWindowSeconds` | 10 | Rooms |
| `ShutdownTimeoutSeconds` | 5 | Rooms |

There is deliberately **no** `Rooms:Server:TickHz`: the tick rate is per room, defaulted by `Rooms:Defaults:TickHz` and set per room by the admin API.

**`Rooms:Quotas`** — per-connection limits (see the quota table in [`protocol.md`](./protocol.md#quota-defaults)).

| Key | Default |
|---|---|
| `MaxConnectionsPerIp` | 8 |
| `MaxMessagesPerSecond` | 60 |
| `MaxBytesPerSecond` | 8192 |
| `MaxPayloadBytes` | 4096 |
| `MaxEntityUpdatesPerFrame` | 8 |
| `MaxSpawnsPerMinute` | 240 |
| `MaxSignalsToServerPerSecond` | 20 |
| `MaxSignalsToAoiPerSecond` | 10 |
| `MaxSignalsToAllPerSecond` | 2 |
| `MaxColdPropsPerSecond` | 2 |
| `MaxResyncPerSecond` | 2 |
| `MaxTeleportsPerMinute` | 12 |
| `IdleTimeoutSeconds` | 60 |
| `MaxChatPerMinute` | 10 |

**`Rooms:Auth`**

| Key | Default |
|---|---|
| `Mode` | `Jwt` (or `Insecure`, development only) |
| `JwtSecret` | *(required when `Mode=Jwt`; ≥32 bytes)* |
| `Issuer` | `pix3-cloud` |
| `Audience` | `pix3-rooms` |
| `ClockSkewSeconds` | 60 |
| `ServiceToken` | *(required in Production; empty ⇒ the admin API denies everything)* |
| `AllowedOrigins` | *(empty = any, development only)* |

**`Rooms:Defaults`** — per-room values substituted when a create request omits them: `MaxPlayers` 64, `TickHz` 20, `AoiRadius` 1200, `IdleTtlSeconds` 300, `MaxEntities` 4096, `MaxVisibleEntities` 64, `Mode` `Relay`, `WorldOriginX` −2048, `WorldOriginY` −2048, `WorldSize` 4096.

**`Metrics`** — `Path` `/metrics`, `RequireServiceToken` true in production, `MaxSeriesPerMetric` 64.

`Auth:Mode=Insecure` is for local development only: it accepts unsigned `dev:<sub>:<roomId>` tokens, logs a loud warning at startup, and **refuses to start** when `ASPNETCORE_ENVIRONMENT=Production`. Production additionally refuses to start without `Rooms:Auth:ServiceToken`.

## Coding rules

- Nullable enabled and `nullable` warnings are errors. No `!` to silence what you can restructure.
- Dependencies: `MemoryPack` (Protocol), `Microsoft.IdentityModel.JsonWebTokens` (Auth), xUnit + `Microsoft.AspNetCore.Mvc.Testing` (tests). **Add nothing else** — metrics are hand-rolled Prometheus text.
- Hot path (anything called per tick per entity or per client): no allocations, no LINQ, no `async` state machines, `Span<byte>`/`ref struct` writers, `[MethodImpl(AggressiveInlining)]` where measured.
- Control path may be idiomatic and allocate.
- Readers of untrusted bytes return `bool` and never throw. Malformed input is a normal event, not an exception.
- Never trust client-supplied ids, ticks, masks or floats. Validate ownership on every entity mutation, finiteness on every float, range on every quantized field.
- Buffer ownership transfers explicitly: whoever successfully enqueues a pooled buffer gives it up; whoever fails to enqueue must return it. Every error and drop path returns its buffers. Double-return is worse than a leak.
- Every `catch` either handles or logs with context; never swallow silently. A throwing room must be destroyed and its clients closed with `InternalError`, not left half-alive.
- XML doc comments on public seam members; brief `//` comments only where the *why* isn't obvious — and always where a subtle invariant is upheld (generation reuse, known-set commit ordering, buffer ownership, `Seq` advancement).

## Tests

A change to a byte layout must come with updated **golden vectors** (hardcoded expected bytes derived from [`protocol.md`](./protocol.md) by hand — never generated by the code under test) and a `ProtocolVersion` bump.

Permanent regression properties:

- **Replication core**: no delta without a prior full record; generation reuse rejected; enter+dirty in one tick sends a full record only; frame-size cap neither loses nor duplicates entities; encode count equals dirty-entity count, not dirty × subscribers; removals precede reuse within a frame.
- **Recovery**: a failed hot-lane enqueue rolls the known set back and leaves `Seq` unchanged; `ResyncCommand` produces a complete snapshot ending with `Final`; a hidden client receives no hot frames and re-snapshots on un-hide.
- **Caps**: `MaxVisibleEntities`, `MaxEntersPerTick` (with carry) and `MaxBytesPerClientPerTick` all hold under a 600-entity dogpile.
- **Quantization**: encode→decode→encode is a fixed point; dirty detection ignores sub-quantum float noise.
- **Room isolation**: two rooms, zero cross-talk — precisely what the predecessor server got wrong.
- **Transport**: the 101 response carries no `Sec-WebSocket-Extensions`.

Run `dotnet build Pix3.Rooms.slnx` (zero warnings) and `dotnet test Pix3.Rooms.slnx` before declaring anything done. Performance claims need a `tools/Pix3.Rooms.LoadGen` run with numbers, not reasoning.
