# Pix3 Rooms — architecture and module contract

`pix3-rooms` is the **Room Fabric** for pix3: a multi-tenant .NET WebSocket server that hosts game rooms for scenes authored in the pix3 editor. It terminates sockets, validates identity, owns room lifecycle and quotas, and replicates generic entity state with area-of-interest filtering. **It never learns pix3 scene semantics** (nodes, components, prefabs) — those stay in `@pix3/runtime` on the client, and later in Level-3 server workers behind this gateway.

Full product context and phasing: `pix3/.plans/multiplayer-platform.md`. Wire contract: [`protocol.md`](./protocol.md).

## Non-negotiable design constraints

The flagship requirement is **600 concurrent players in one room, 2D top-down shooter, without lag**. That dictates the hot path:

1. **AOI is core, not an optimization.** Broadcast-all at 600 players is ~4.3 Gbit/s — dead on arrival. A uniform spatial hash restricts each client to the ~30–50 entities near it.
2. **Encode-once, memcpy-many.** Per tick, each dirty entity's `DeltaRecord` bytes are written **once** into a scratch buffer; per-client packets are assembled by copying those byte ranges. Never re-serialize per recipient.
3. **Zero allocation on the tick path.** Structure-of-arrays state, pre-allocated buffers, `ArrayPool<byte>` for frames, bitsets for AOI membership. No LINQ, no `foreach` over interfaces, no lambda captures, no `List<T>` growth inside a tick.
4. **Room = isolated unit.** Each room owns its state, its tick loop and its budget. One heavy room must never stall another (the WsCore reference server has a single global loop — we deliberately do not).
5. **Single-threaded room logic.** Sockets hand inbound messages to a per-room queue; the room drains it at tick start. Room state is touched by exactly one thread at a time, so no locks in game logic.
6. **Nothing game-specific.** No players/HP/bullets/weapons in this repo. Entities are `(netId, owner, kind, transform, flags, cold props)`.

## Reference implementation to learn from

`WsCore` (the predecessor experiment, path supplied per task) has a battle-tested socket layer worth porting in spirit:

- `WsServer/WsServer/WebSocketHandler.cs` — frame reassembly to `EndOfMessage`, per-connection bounded `Channel` (cap 256, `DropOldest`) + a single send loop, consecutive-error cutoff, message-size cap.
- `Shared/MessageSerializer.cs` — the `[TypeId][payload]` framing.
- `Shared/ReflectionServerLogicProvider.cs` — reflection handler discovery with boot-time duplicate-TypeId detection.

Things it got wrong that we must **not** copy: one global `GameModel` shared by all rooms; global broadcast ignoring room membership; no AOI (its docs claim otherwise); no auth; a JSON text-frame fallback that reflects properties while DTOs use fields; `[Flags]` enum with values 0/1/2/3; `CreateRoom` ignoring `TryAdd`.

## Module map and ownership

One folder = one owner. Do not create or edit files outside your folder; if you need something from another module, use the seam below exactly as declared.

| Folder | Namespace | Responsibility |
|---|---|---|
| `src/Pix3.Rooms.Protocol/` | `Pix3.Rooms.Protocol` | Wire contract: MemoryPack messages, TypeId map, hand-packed hot codecs, version const, reject codes |
| `src/Pix3.Rooms.Server/Net/` | `Pix3.Rooms.Server.Net` | Kestrel WS endpoint, connection objects, send queues, inbound decode + dispatch, handshake, quotas/rate limits |
| `src/Pix3.Rooms.Server/Auth/` | `Pix3.Rooms.Server.Auth` | Room-token (JWT) validation, service-token validation, dev/insecure mode |
| `src/Pix3.Rooms.Server/Rooms/` | `Pix3.Rooms.Server.Rooms` | Room, room manager/registry, per-room tick loop, TTL eviction, membership, room-scoped fan-out, chat, room vars |
| `src/Pix3.Rooms.Server/Replication/` | `Pix3.Rooms.Server.Replication` | Entity table (SoA), spatial hash AOI, per-subscriber known-sets, encode-once delta/snapshot assembly |
| `src/Pix3.Rooms.Server/Admin/` | `Pix3.Rooms.Server.Admin` | Admin REST API for room lifecycle (service-token auth), `/health` |
| `src/Pix3.Rooms.Server/Observability/` | `Pix3.Rooms.Server.Observability` | Dependency-free metrics registry + Prometheus text endpoint |
| `src/Pix3.Rooms.Server/Program.cs`, `appsettings*.json` | `Pix3.Rooms.Server` | Composition root: options binding, DI, endpoint wiring |
| `tests/Pix3.Rooms.Tests/` | `Pix3.Rooms.Tests` | xUnit: golden wire vectors, AOI, room lifecycle, quotas, auth |
| `tools/Pix3.Rooms.LoadGen/` | `Pix3.Rooms.LoadGen` | Headless load generator: N bot clients against a room, latency/bandwidth report |

Dependencies flow one way: `Protocol` ← `{Net, Rooms, Replication, Auth, Admin}`; `Net` → `Rooms` (enqueue) and `Auth`; `Rooms` → `Replication` and `Net` (send); `Admin` → `Rooms`. `Replication` depends on `Protocol` only — it must be unit-testable with no sockets.

## Cross-module seams (implement these signatures verbatim)

```csharp
// ── Net ────────────────────────────────────────────────────────────────────
namespace Pix3.Rooms.Server.Net;

/// A live socket. Room logic only ever sees this interface.
public interface IClientConnection
{
    uint   ClientId    { get; }          // room-unique, monotonic per server
    string RemoteIp    { get; }
    string DisplayName { get; }
    bool   IsOpen      { get; }
    /// Enqueue an already-encoded frame. False = queue full or closed (caller must return the buffer).
    bool TryEnqueue(in OutboundFrame frame);
    /// Send RejectEvent (when code != None) and close with the mapped WS status.
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
    public int     MaxEntities        { get; init; } = 4096;
    public RoomMode Mode              { get; init; } = RoomMode.Relay;
}

public readonly struct InboundMessage
{
    public readonly uint ClientId;
    public readonly byte TypeId;
    public readonly byte[] Payload;   // rented; the room returns it to FramePool after handling
    public readonly int Length;
}

public interface IRoom
{
    RoomConfig Config      { get; }
    int        PlayerCount { get; }
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset LastActivityAt { get; }
    bool TryJoin(IClientConnection connection, out RejectCode reject);
    void Leave(uint clientId, LeaveReason reason);
    /// Non-blocking; false = room inbound queue full (caller drops + counts).
    bool TryEnqueueInbound(in InboundMessage message);
    Task RunAsync(CancellationToken cancellationToken);
    RoomStats SnapshotStats();
}

public sealed record RoomStats(int PlayerCount, int EntityCount, uint ServerTick,
                               double TickMsP50, double TickMsP99, long BytesOutPerSecond,
                               long DroppedFrames, long BudgetOverruns);

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

/// Owns entity state, AOI and all hot-path encoding for ONE room. Single-threaded by contract.
public interface IRoomReplication
{
    int EntityCount { get; }
    bool TrySpawn(uint ownerId, ushort kind, in EntityWireState state, out uint netId, out RejectCode reject);
    bool TryDespawn(uint netId, uint requesterId, out RejectCode reject);
    /// Applies one client delta record; false when not owned / unknown / illegal mask.
    bool TryApplyOwnedUpdate(uint netId, uint ownerId, byte mask, in EntityWireState state);
    /// Despawns everything owned by a leaving client; appends removed ids to `despawned`.
    void RemoveOwner(uint ownerId, List<uint> despawned);

    void AddSubscriber(uint clientId);
    void RemoveSubscriber(uint clientId);
    /// AOI centre for this client (normally its own avatar's position).
    void SetSubscriberFocus(uint clientId, float x, float y);

    /// Rebuild grid, recompute visibility, fill encode-once scratch. Call once per tick.
    void Tick(uint serverTick);
    /// Writes a complete SnapshotFrame (TypeId included) for a joiner. Returns bytes written, 0 if none.
    /// `continuationCursor` lets a large snapshot be emitted across several frames; pass 0 to start.
    int WriteSnapshot(uint clientId, Span<byte> destination, ref int continuationCursor);
    /// Writes a complete DeltaFrame (TypeId included). Returns 0 when this client has nothing to receive.
    int WriteDelta(uint clientId, Span<byte> destination);
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
```

## Runtime topology

```
Kestrel  ──/ws?room=<id>──►  WebSocketEndpoint
                                │  accept, IP cap, Hello, token validate, room lookup
                                ▼
                            IRoom.TryJoin ──► per-room inbound Channel (bounded)
                                                     │
                     per-room async loop (PeriodicTimer @ TickHz, own budget stopwatch)
                       drain inbound ─► apply to IRoomReplication ─► Tick(serverTick)
                       ─► per client: WriteDelta ─► IClientConnection.TryEnqueue
                                                     │
                                    per-connection bounded Channel ─► send loop ─► socket
```

Admin REST (`/admin/rooms`, service token) creates and destroys rooms; a sweeper destroys rooms that have been empty longer than `IdleTtlSeconds`.

## Configuration (appsettings, section `Rooms`)

```
Rooms:
  Server:   { TickHz, MaxRooms, MaxTotalConnections, InboundQueueCapacity, OutboundQueueCapacity }
  Quotas:   { MaxConnectionsPerIp, MaxMessagesPerSecond, MaxBytesPerSecond, MaxPayloadBytes,
              MaxEntityUpdatesPerFrame, MaxSpawnsPerMinute, IdleTimeoutSeconds, MaxChatPerMinute }
  Auth:     { Mode: Jwt|Insecure, JwtSecret, Issuer, Audience, ClockSkewSeconds, ServiceToken }
  Defaults: { MaxPlayers, AoiRadius, IdleTtlSeconds, MaxEntities, TickHz }
```

`Auth:Mode=Insecure` is for local development only: it accepts unsigned `dev:<sub>:<roomId>` tokens, logs a loud warning at startup, and must refuse to start when `ASPNETCORE_ENVIRONMENT=Production`.

## Coding rules

- Nullable enabled and `nullable` warnings are errors. No `!` to silence what you can restructure.
- Dependencies: `MemoryPack` (Protocol), `Microsoft.IdentityModel.JsonWebTokens` (Auth), xUnit (tests). **Add nothing else** — metrics are hand-rolled Prometheus text.
- Hot path (anything called per tick per entity or per client): no allocations, no LINQ, no `async` state machines, `Span<byte>`/`ref struct` writers, `[MethodImpl(AggressiveInlining)]` where measured.
- Control path may be idiomatic and allocate.
- Every `catch` either handles or logs with context; never swallow silently. A throwing room must be destroyed and its clients closed with `InternalError`, not left half-alive.
- XML doc comments on public seam members; brief `//` comments only where the *why* isn't obvious from the code.
