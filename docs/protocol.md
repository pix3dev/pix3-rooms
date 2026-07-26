# Pix3 Rooms wire protocol — v2

Authoritative spec. `Pix3.Rooms.Protocol` implements exactly this; clients (pix3 runtime, LoadGen) speak exactly this. Any change to a byte layout is a **protocol version bump**.

**v2 is the first version that ships.** v1 was never spoken by a client and is not supported: `MinSupported = Current = 2`. See the [change log](#change-log) for what moved and why.

## Framing

Every WebSocket **binary** frame is one message:

```
[u8 TypeId][payload …]
```

Text frames are **rejected** (close 4007). All integers are **little-endian**. Floats are IEEE-754 `float32`.

Payloads come in two flavours:

- **Control plane** — MemoryPack-serialized classes (`[MemoryPackable]`). Low rate, schema-evolvable, readable. Every control message is **version-tolerant with explicit member ordering** (`[MemoryPackable(GenerateType.VersionTolerant)]` + `[MemoryPackOrder(n)]` on every member), so a field can be appended without a version bump.
- **Hot plane** (`TypeId` 67/68/69/130, suffix `…Packet`) — **hand-packed** fixed layouts, described below. These are on the 600-players-per-room path: they must be written with zero allocations and support *encode-once, memcpy-many* fan-out. Never MemoryPack these.

Length prefixing belongs to the **transport**, not to these layouts: a WebSocket frame already carries its length, and a future WebTransport datagram carries its own. The byte layouts here are therefore identical on both.

## Version negotiation

`ProtocolVersion.Current = 2`, `ProtocolVersion.MinSupported = 2`.

The **first** frame a client sends must be `HelloCommand` (TypeId 1). Anything else → `RejectedEvent{BadRequest}` + close 4007.

Negotiation is **by range, not equality**:

1. The client announces in `HelloCommand.ProtocolVersion` the **highest** version it speaks.
2. `version < MinSupported` → `RejectedEvent{ProtocolVersionMismatch}` + close 4001, with a human-readable message. Never a decoder error.
3. Otherwise the session runs at `min(client, Current)`, and `WelcomeEvent.ProtocolVersion` echoes that **negotiated** version. Both sides speak it for the whole session.

**Unknown TypeId is ignored and counted, never fatal — in both directions.** That is what lets a game published six months ago keep working when the fabric adds messages. A sustained stream of unknown ids is still abuse and trips the consecutive-protocol-error cutoff.

Successful handshake: `HelloCommand` → validate token → resolve room → join → `WelcomeEvent`, then `RoomVarsChangedEvent`, then one or more `SnapshotPacket`s (the last with `Final` set), then `PeerJoinedEvent` fan-out to the others.

## TypeId allocation

Ranges are reserved; do not allocate outside them. A `MessageTypeIds` constant is spelled exactly like its class, so one grep finds a message's whole path.

| Range | Purpose |
|---|---|
| 0–63 | Core: handshake, session, chat, room vars, client prefs |
| 64–127 | State sync (entities) |
| 128–191 | Signals (networked game events) |
| 192–255 | Reserved for app/game-specific extensions — the fabric never interprets these and drops them with a counter |

### Core (0–63)

| Id | Name | Dir | Fields |
|---|---|---|---|
| 1 | `HelloCommand` | C→S | `ushort ProtocolVersion`, `string Token`, `string RoomId`, `string DisplayName`, `ushort Capabilities`, `byte[]? ResumeKey` |
| 2 | `WelcomeEvent` | S→C | see [below](#welcomeevent) |
| 3 | `RejectedEvent` | S→C | `ushort Code` (`RejectCode`), `string Message` |
| 4 | `PingCommand` | C→S | `long ClientTimeMs` |
| 5 | `PongEvent` | S→C | `long ClientTimeMs`, `long ServerTimeMs`, `uint ServerTick` |
| 6 | `PeerJoinedEvent` | S→C | `uint ClientId`, `string DisplayName` |
| 7 | `PeerLeftEvent` | S→C | `uint ClientId`, `byte Reason` (`LeaveReason`) |
| 8 | `RoomInfoEvent` | S→C | `ushort PlayerCount`, `ushort EntityCount`, `uint ServerTick` — sent ~1 Hz |
| 9 | `SendChatCommand` | C→S | `string Text` |
| 10 | `ChatMessageEvent` | S→C | `uint ClientId`, `string Text` |
| 11 | `LeaveCommand` | C→S | *(empty)* |
| 12 | `SetRoomVarCommand` | C→S | `string Key`, `byte[] Value` |
| 13 | `RoomVarsChangedEvent` | S→C | `string[] Keys`, `byte[][] Values` — full set on join, changed subset afterwards |
| 14 | `ResyncCommand` | C→S | *(empty)* — "my known set is untrustworthy, re-send it" |
| 15 | `SetClientPrefsCommand` | C→S | `bool Hidden`, `byte SendRateDivisor` |
| 16 | `HostChangedEvent` | S→C | `uint HostClientId`, `uint PreviousHostClientId` |

#### `WelcomeEvent`

| Field | Meaning |
|---|---|
| `uint ClientId` | Room-unique id for this session. Preserved across a successful resume. |
| `string RoomId` | The room actually joined. |
| `byte TickHz` | Room tick rate. |
| `long ServerTimeMs` | Server clock at send, for offset estimation. |
| `uint ServerTick` | Tick at join. |
| `float AoiRadius` | AOI **enter** radius (exit is `1.25 ×` this). |
| `ushort MaxPlayers` | Room member cap. |
| `ushort ProtocolVersion` | The **negotiated** session version. |
| `float WorldOriginX`, `float WorldOriginY`, `float WorldSize` | World bounds this room quantizes against (see [Quantization](#quantization)). |
| `byte Mode` | `RoomMode`: `0 Relay` (client authority, Level 1), `1 Authoritative`. |
| `ushort MaxVisibleEntities` | Hard cap on entities this client can be told about at once — a sizing hint for its receive tables. |
| `uint HostClientId` | Current host (`0` when none). See [Authority](#authority-and-ownership). |
| `byte[] ResumeKey` | 16 bytes, **regenerated on every connect**. Present it in `HelloCommand.ResumeKey` to resume. |
| `bool Resumed` | True when this `WelcomeEvent` answered a successful resume: the client's entities are still alive and its known set was rebuilt, so it must not reset its local state. |

### State (64–127)

| Id | Name | Dir | Encoding |
|---|---|---|---|
| 64 | `SpawnEntityRequest` | C→S | MemoryPack: `uint RequestId`, `ushort Kind`, `ushort QX`, `ushort QY`, `byte QRot`, `short QVx`, `short QVy`, `byte Flags`, `byte[]? Props` |
| 65 | `SpawnEntityResponse` | S→C | MemoryPack: `uint RequestId`, `uint NetId`, `ushort RejectCode` (0 = ok) |
| 66 | `DespawnEntityCommand` | C→S | MemoryPack: `uint NetId` |
| 67 | `EntityUpdatePacket` | C→S | **hand-packed** (below) |
| 68 | `SnapshotPacket` | S→C | **hand-packed** (below) |
| 69 | `DeltaPacket` | S→C | **hand-packed** (below) |
| 70 | `SetEntityPropsCommand` | C→S | MemoryPack: `uint NetId`, `byte[] Json` |
| 71 | `EntityPropsChangedEvent` | S→C | MemoryPack: `uint NetId`, `byte[] Json` |

Spawn carries **quantized** fields, not floats: the quantized integers are the replicated values everywhere (see [Quantization](#quantization)), so a spawn must not introduce a value the delta plane could not have expressed.

### Signals (128–191)

A **signal** is a networked game event (pix3's own term — one word, one concept). `Target` selects the routing.

| Id | Name | Dir | Encoding |
|---|---|---|---|
| 128 | `EmitSignalCommand` | C→S | MemoryPack: `string Name`, `byte Target` (`SignalTarget`), `uint TargetClientId`, `byte[] Payload` |
| 129 | `SignalEvent` | S→C | MemoryPack: `uint SenderClientId`, `string Name`, `byte[] Payload` |
| 130 | `SignalBatchPacket` | S→C | **hand-packed** (below) |

Delivery split, by target:

- `Server` (0) — handled by the room (Level 2/3); nothing is fanned out.
- `AllPeers` (1) and `SinglePeer` (2) — delivered as `SignalEvent`, one frame per recipient. Low rate by quota (2/s and 20/s), so the control plane is the right home.
- `AoiPeers` (3) — batched into one `SignalBatchPacket` per recipient per tick, assembled with the same encode-once/memcpy-many discipline as the delta and flushed alongside it. This is the path a shooter's fire events take (see [Projectiles](#projectiles-are-not-entities)); it must never cost an extra socket send.

## Hot plane

### Quantization

World bounds are declared **per room** (`WorldOriginX`, `WorldOriginY`, `WorldSize`; defaults `−2048, −2048, 4096`) and echoed in `WelcomeEvent`.

| Field | Wire | Encode | Decode | Precision |
|---|---|---|---|---|
| X, Y | `u16` | `clamp(round((v − origin) × 65535 / WorldSize), 0, 65535)` | `origin + q × WorldSize / 65535` | 1/16 unit at `WorldSize = 4096` |
| Rot | `u8` | `round(((rot mod 2π) + 2π mod 2π) / 2π × 256) & 0xFF` | `q × 2π / 256` | 1.41° |
| Vx, Vy | `i16` | `clamp(round(v × 8), −32768, 32767)` | `q / 8` | 0.125 u/s, ±4095 u/s |

Records stay **byte-aligned** — no bit-packing — because memcpy fan-out is worth more than the last 15%.

**The normative quantize-on-both-sides rule.** The quantized integers *are* the replicated values:

- The server stores dequantized-from-quantized floats as authoritative state. It never stores a float a client sent verbatim.
- An owning client publishes quantized values and **renders its own entity from the same dequantized values**, so nobody chases divergence pops.
- **Dirty detection compares quantized integers, never floats.** This is not an optimization: comparing floats would keep an idle entity dirty forever on sub-quantum noise.

### Flags byte

The `Flags` byte travels in every full record and is maskable in updates.

| Bits | Owner | Meaning |
|---|---|---|
| 0–1 | fabric | **Ownership policy**: `0 Owned` (despawned when its owner leaves), `1 Shared` (reassigned to the new host), `2 Transferable` (reassignable to any client), `3` reserved |
| 2 | fabric | Reserved; must be sent as 0 and ignored on receipt |
| 3–7 | app | Game-defined bits, replicated verbatim. The fabric never interprets them |

### Delta mask bits (`DeltaMask`)

| Bit | Name | Payload |
|---|---|---|
| 0x01 | `X` | `u16 QX` |
| 0x02 | `Y` | `u16 QY` |
| 0x04 | `Rot` | `u8 QRot` |
| 0x08 | `Vx` | `i16 QVx` |
| 0x10 | `Vy` | `i16 QVy` |
| 0x20 | `Flags` | `u8 Flags` |
| 0x40 | `ColdDirty` | *(no bytes)* — cold props changed; expect `EntityPropsChangedEvent` |
| 0x80 | `Teleport` | *(no bytes)* — discontinuity; the receiver must snap, not interpolate |

Fields appear in **bit order**. Client→server masks are limited to `0x3F | 0x80`; anything else increments the `mask` violation counter and drops the record.

Velocity stays in the format and in the mask vocabulary but is **off the wire by default**: at 20 Hz, linear interpolation of 2D sprites does not need it. A typical moving entity costs **8 B** (`u16 Slot` + mask + QX + QY + QRot).

### `FullRecord` — 20 bytes, fixed

```
u32 NetId
u16 Kind
u32 OwnerId
u16 QX, u16 QY
u8  QRot
i16 QVx, i16 QVy
u8  Flags
```

### `UpdateRecord` (S→C) — 3…13 bytes

```
u16 Slot
u8  Mask
(u16 QX)(u16 QY)(u8 QRot)(i16 QVx)(i16 QVy)(u8 Flags)   — present per Mask, in bit order
```

### `OwnerUpdateRecord` (C→S) — 5…15 bytes

```
u32 NetId
u8  Mask
(u16 QX)(u16 QY)(u8 QRot)(i16 QVx)(i16 QVy)(u8 Flags)   — present per Mask, in bit order
```

Same fields as `UpdateRecord`, keyed by **`NetId`** rather than slot: the server needs the generation bits to reject a mutation aimed at a slot that has since been reused.

### Slot addressing (S→C only)

Server→client traffic addresses entities by **`u16 Slot`**, not `netId`. The client's known set maps slot → netId, learned from the `FullRecord` that introduced the entity.

Two rules make this safe on an ordered stream:

1. **Removals are processed first within a frame**, before enters and updates, so a slot's removal always precedes any reuse of it.
2. `MaxEntities ≤ 65535` (default 4096), so a slot always fits in `u16`.

Removal record — **2 bytes**: `u16 Slot`.

### `SnapshotPacket` (68, S→C) — 10-byte header

```
u8  TypeId = 68
u16 Seq
u32 ServerTick
u8  FrameFlags        — bit 0 = Final; bits 1–7 reserved, sent 0
u16 Count
FullRecord × Count
```

Sent right after `WelcomeEvent` (entities inside the joiner's AOI) and after a resync. A large snapshot is split across several frames, each self-contained; only the last carries `Final`. Without that bit a client had no way to know a multi-frame snapshot was complete.

### `DeltaPacket` (69, S→C) — 13-byte header

```
u8  TypeId = 69
u16 Seq
u32 ServerTick
u16 RemovedCount ; u16 Slot       × RemovedCount   — despawned OR left AOI
u16 EnterCount   ; FullRecord     × EnterCount     — entered AOI
u16 UpdateCount  ; UpdateRecord   × UpdateCount    — already-known entities that changed
```

A tick with nothing for a given client produces **no frame at all**.

### `EntityUpdatePacket` (67, C→S)

```
u8  TypeId = 67
u32 ClientTick
u8  Count
OwnerUpdateRecord × Count
```

Server rules: every `NetId` must be owned by the sender (else the record is dropped and the `ownership` counter increments), `Count` is quota-capped, every quantized field is range-checked, and the server stamps its own tick — the client tick is advisory only (telemetry and ordering).

### `SignalBatchPacket` (130, S→C) — 8-byte header

```
u8  TypeId = 130
u16 Seq
u32 ServerTick
u8  Count
Entry × Count:
    u32 SenderClientId
    u8  NameLength                 — 1…64
    u8[NameLength] Name            — UTF-8
    u8  PayloadLength              — 0…255
    u8[PayloadLength] Payload
```

One packet per client per tick, flushed with that client's delta. A signal whose payload exceeds 255 bytes is not eligible for the hot path and is refused with the `quota` counter — batched signals are small game events, not a data channel.

## Sequencing and recovery

The lossy link is **not the network** — WSS is TCP — it is our own bounded send queue. Position updates self-heal because they carry absolute values and are re-sent whenever dirty; **enter and removal records do not**. A dropped frame would otherwise leave a client with a permanent ghost entity or a permanently invisible one.

Three mechanisms, in the order they matter:

1. **Two-phase known-set commit — the actual fix.** A known-set bit may be flipped **only after** the frame carrying that enter/removal has been accepted by the send queue. The writer records what it *intends* to set; the caller commits on a successful enqueue and rolls back otherwise. See `IRoomReplication` in [`architecture.md`](./architecture.md).
2. **`u16 Seq` — the detector.** Every server→client hot frame (`SnapshotPacket`, `DeltaPacket`, `SignalBatchPacket`) carries a per-client counter, incremented **only when a frame is actually emitted**, wrapping mod 2¹⁶. Client rule: a gap (`seq != last + 1 mod 2¹⁶`) means desync — send `ResyncCommand` and ignore hot frames until the next snapshot. Cost: 2 B/frame ≈ 0.3 kbit/s.
3. **`ResyncCommand` — the cure.** The server clears that client's known-set bitset and re-sends a full snapshot on the next tick through the existing continuation cursor. One primitive covers queue overflow, tab refocus, reconnect and future datagram loss. A resync costs ~40 × 20 B ≈ 800 B, and is itself quota-limited.

Send-queue lanes are part of this contract: the **control lane** must never drop silently (a failed enqueue closes the connection), while a failed **hot-lane** enqueue returns the buffer and marks the client for resync.

## Bandwidth caps

An AOI *radius* does not bound worst-case egress: 600 players stacked on one point all see each other. Three caps turn the ceiling from a hope into a guarantee:

| Cap | Default | Meaning |
|---|---|---|
| `MaxVisibleEntities` | 64 | Per client, **k-nearest by squared distance**. Entities beyond the cap are simply not replicated to that client this tick. |
| `MaxEntersPerTick` | 24 | New full records per client per tick, with a **carry cursor** so the remainder arrives on following ticks instead of being lost. |
| `MaxBytesPerClientPerTick` | 1100 | One MSS, and one future QUIC datagram. Assembly emits what fits and carries the rest. |

Expected steady state: ~30 moving entities × 8 B + 13 B header ≈ 280 B/tick with framing → **45 kbit/s per client, ~27 Mbit/s per room**. Absolute worst case with the caps biting: 176 kbit/s per client, 105 Mbit/s per room.

**AOI hysteresis**: an entity **enters** at `AoiRadius` and **exits** only beyond `1.25 × AoiRadius`. At 600 players the arena edge is all boundary, and a flapping pair costs ~22 B/tick each.

**AOI focus comes from server state, not from a client claim.** A subscriber's focus is bound to one of its owned entities' *server-side* position, refreshed every tick. Free-position focus exists only for spectators and is speed-clamped, incrementing `focusClamp` when it bites. This deletes the "teleport my focus every tick to force enormous enter sets and amplify to N peers" exploit at its source; the enter cap bounds whatever remains.

## Authority and ownership

The room's `Mode` travels in `WelcomeEvent`, and the rules are part of the wire contract — which is what makes Level-2 server validation a zero-byte, non-breaking upgrade rather than a protocol break:

- You **simulate and publish what you own**. An entity you own is yours to move; nobody else's update for it is accepted.
- `OwnerId == 0` means **server-owned**: read-only to every client.
- A server update for *your* entity is an **authoritative correction** — snap to it.
- Ownership policy lives in the [flags byte](#flags-byte). When an owner leaves: `Owned` entities are despawned; `Shared` entities are reassigned to the new host; `Transferable` entities may be reassigned to any client.
- **Host migration**: the host is the longest-present member. When the host leaves, the room promotes the next-longest-present member, reassigns `Shared` entities to it, and announces `HostChangedEvent`. (Without this, a departing host's pickups vanish and every public "play with friends" session dies when its creator backgrounds their phone.)

`HostChangedEvent` is defined here so its id is reserved and clients can be written against it; server emission lands with host migration in Phase 1–2. Because unknown TypeIds are ignored rather than fatal, a client may be written for it before the server sends it.

## Reconnect and resume

`WelcomeEvent` issues a fresh **16-byte resume key** on every connect (regenerated each time, so a leaked key cannot be replayed for a later session). A client may present it in `HelloCommand.ResumeKey`.

Within a **30-second grace** after a socket drops:

- the client keeps its `ClientId` and its entities, which **freeze in place**;
- `PeerLeftEvent` is deferred — peers are not told about a blip;
- a successful resume answers with `WelcomeEvent{Resumed = true}` followed by a fresh snapshot (the known set is rebuilt from scratch, never assumed);
- a **failed** resume silently degrades to a fresh join. No new error paths: a stale, wrong or expired key is simply not a resume.

After the grace expires the member leaves with `LeaveReason.Timeout` and its entities are resolved by ownership policy.

## Client preferences and hidden tabs

`SetClientPrefsCommand{Hidden, SendRateDivisor}`:

- `Hidden = true` — the server **suspends that client's hot plane entirely** (no deltas, no snapshots, no signal batches; `Seq` stops advancing) and re-snapshots on un-hide. Chrome throttles timers to once per second in hidden tabs and once per *minute* after five minutes, and `requestAnimationFrame` stops outright: a backgrounded tab cannot drain a 20 Hz stream, it buffers it.
- `SendRateDivisor` — `0` or `1` means every tick; `n > 1` means this client is served every `n`th tick. Clamped to `[1, 8]`. Control-plane messages are unaffected.

## Input validation

Every inbound value is validated before it can touch room state. These are not defensive niceties: a single packet could otherwise take a room down.

| Check | Rule | Counter |
|---|---|---|
| Finiteness | every inbound float must be finite — **one NaN poisons the spatial hash** | `nan` |
| Bounds | positions clamped to the world AABB | `nan` |
| Quantized range | every quantized field range-checked before dequantization | `mask` |
| Ownership | every entity mutation must come from its owner | `ownership` |
| Mask legality | client masks limited to `0x3F \| 0x80` | `mask` |
| Kind | a per-room **allowlist** of entity kinds; an unknown `kind` would fault every observer's scene code | `kind` |
| Speed | `\|Δpos\| ≤ maxSpeed × Δt × 1.25` — **counted, not enforced**, at Level 1 | `speed` |
| Focus | spectator focus movement speed-clamped | `focusClamp` |
| Teleport bit | legitimate under client authority (respawns); quotaed now, stripped at Level 2 | `teleport` |
| Quotas | see below | `quota` |

`Kind` indexes the **build's prefab table**: the manifest emitted by the pix3 exporter and the room's allowlist must agree. Until the exporter emits that table, dev rooms allow any kind and production rooms require an explicit list.

Violations increment **per-client counters** (`ownership, speed, mask, nan, kind, quota, focusClamp, teleport`) exposed through the admin API. Build the dataset now, the detector later. The Level-1 speed check *is* the Level-2 validator, written early behind the same seam.

### Quota defaults

| Quota | Default |
|---|---|
| Inbound messages | 60/s per connection |
| Inbound bytes | 8 KiB/s per connection |
| Inbound payload | 4 KiB per frame |
| Entity updates | 8 records per `EntityUpdatePacket` |
| Spawns | 240/min per connection |
| Entities per owner | 64 |
| Signals → server | 20/s |
| Signals → AOI peers | 10/s |
| Signals → all peers | **2/s** (a 600× amplifier) |
| Chat | 10/min, ≤240 chars |
| Cold props | ≤512 B, 2/s per entity |
| Resync requests | 2/s per connection |
| Connections per IP | 8, plus 4 pre-authentication |
| Teleport bits | 12/min (soft — counted) |

## `NetId`

`netId` is an opaque `uint`: `slot | (generation << 16)` — **16 bits slot, 16 bits generation**.

- Server→client records address entities by `u16 Slot`, which caps `MaxEntities` at 65535 anyway, so slot bits beyond 16 are unusable. Spending them on generations gives 65 536 reuses per slot instead of 4 096, for free.
- **Generations start at 1**, so `0` is permanently a safe "no entity" sentinel.
- A `(slot, generation)` pair is **never reused within a room's lifetime**; a slot whose generation is exhausted is retired, never wrapped (practically unreachable at 16 bits).
- Clients treat the value as opaque and never compute slots or generations from it.

## Reject codes (`RejectCode : ushort`)

| Value | Name | WS close |
|---|---|---|
| 0 | `None` | — |
| 1 | `ProtocolVersionMismatch` | 4001 |
| 2 | `InvalidToken` | 4002 |
| 3 | `TokenExpired` | 4002 |
| 4 | `TokenRoomMismatch` | 4002 |
| 5 | `RoomNotFound` | 4003 |
| 6 | `RoomFull` | 4003 |
| 7 | `RoomClosing` | 4003 |
| 8 | `RateLimited` | 4004 |
| 9 | `PayloadTooLarge` | 4004 |
| 10 | `QuotaExceeded` | 4004 |
| 11 | `ServerShuttingDown` | 4005 |
| 12 | `IdleTimeout` | 4006 |
| 13 | `BadRequest` | 4007 |
| 14 | `SessionReplaced` | 4008 |
| 15 | `EntityLimitReached` | — (spawn response only) |
| 16 | `NotEntityOwner` | — (spawn/despawn response only) |
| 17 | `InternalError` | 4000 |
| 18 | `KindNotAllowed` | — (spawn response only) |

## Leave reasons (`LeaveReason : byte`)

`0 Disconnected`, `1 LeftVoluntarily`, `2 Kicked`, `3 Timeout`, `4 RoomClosed`, `5 Error`.

A drop inside the resume grace emits **no** `PeerLeftEvent` at all; `Timeout` is what peers see when the grace expires.

## Signal targets (`SignalTarget : byte`)

`0 Server`, `1 AllPeers`, `2 SinglePeer`, `3 AoiPeers`.

## Projectiles are not entities

Thousands of bullets as replicated entities was this design's biggest hidden cost. A projectile is fully described by origin + direction + speed + spawn tick, so:

- **Firing is a signal scoped to AOI peers**, and every client simulates the bullet locally.
- Hits are resolved by the owning client at Level 1 and announced as game events. **The fabric never sees a bullet.**

Precedent is strong: Source's one-shot effects are *temporary entities* that never get an edict and never count against the networked-entity limit; Halo: Reach replicates ragdoll initial state only.

The cost moves from bandwidth to per-message overhead, which is exactly what `SignalBatchPacket` absorbs: worst case ~10 events/tick × 21 B ≈ 34 kbit/s and **zero** extra sends.

## Invariants a conforming implementation must uphold

1. **One entity update path**: a client may only mutate entities it owns (L1 client authority).
2. **A known-set bit is set only after the frame carrying it was accepted for sending.** No enter or removal may be assumed delivered before its frame is enqueued.
3. **No delta without a prior full record.** An entity's first appearance for a client is always a `FullRecord`; enter-and-dirty in the same tick sends the full record only.
4. **`Seq` advances only on an emitted frame**, monotonically per client, wrapping mod 2¹⁶.
5. `NetId` is opaque to clients; a `(slot, generation)` pair is never reused within a room's lifetime.
6. **Removals precede reuse**: within a frame, removals are applied before enters and updates.
7. Servers never trust client-supplied ticks, ids, masks or floats without validation.
8. Hot frames are ≤ `MaxBytesPerClientPerTick` (1100 B) and self-contained; control frames are ≤ `MaxPayloadBytes` (4 KiB). The server splits its own oversized snapshots rather than exceeding either.
9. **Unknown TypeIds are ignored and counted**, never fatal, in both directions.
10. Every close whose reason is known is preceded by a `RejectedEvent`, so the client can show a real message.
11. The quantized integers are the replicated values, on both sides, including for dirty detection.

## Reserved for the datagram era

Zero code now. Every choice above respects the contract, so moving the hot plane to WebTransport datagrams later is a **transport** change rather than a protocol change: hot frames are ≤1100 B and self-contained, every hot frame carries `Seq`, `ResyncCommand` exists, length prefixing belongs to the transport, and there is no compression context and no cross-frame state except the known set — which resync can rebuild.

Two mechanisms are specified now and implemented then, behind stubbed seams:

- **Round-robin refresh**: 1/32 of each client's known set re-sent as full records every tick, refreshing the whole AOI in 1.6 s.
- **Keepalive frame**: an empty `DeltaPacket` every 10 ticks so `Seq` keeps advancing on quiet ticks and a gap is detectable promptly.

Explicitly **not** coming: ack bitfields and delta-from-acknowledged baselines (TCP already orders and retransmits; resync covers our own queue, and 32 known-set snapshots per client would cost ~10 MB per room), bit-packing, varints, smallest-three quaternions, any stream compression (breaks memcpy fan-out), per-kind hot schemas, and `permessage-deflate` — which is actively refused, since context takeover breaks datagram portability.

## Change log

### v2 (2026-07-27) — the first version that ships

**Correctness**

- Two-phase known-set commit, `u16 Seq` on every server→client hot frame, and `ResyncCommand`. v1 flipped known-set bits while composing a frame the send queue could then drop, with no recovery primitive of any kind: a dropped enter left a permanent invisible entity, a dropped removal a permanent ghost.
- Send-queue lanes replace `DropOldest` (which also leaked the rented buffer): control-lane failure closes the connection, hot-lane failure marks the client for resync.
- `FrameFlags.Final` on `SnapshotPacket` — v1 gave a client no way to know a multi-frame snapshot was complete.
- Dirty detection compares quantized integers, so float noise can no longer keep an idle entity dirty forever.

**Bandwidth** — v1's float32 layout had zero headroom (~100–113 Mbit/s per room *before* any spike, 86–100% of budget).

- Quantized state: `FullRecord` 31 B → **20 B**, updates 5…26 B → **3…13 B**, removals 4 B → **2 B**, and server→client addressing by `u16 Slot`. A typical moving entity: **8 B**.
- Velocity off the wire by default.
- Three caps make the ceiling a guarantee: `MaxVisibleEntities = 64` (k-nearest), `MaxEntersPerTick = 24` (carry cursor), `MaxBytesPerClientPerTick = 1100`.
- AOI hysteresis at 1.25×; AOI focus bound to server-side position instead of a client claim.
- Projectiles left the entity model entirely; `SignalBatchPacket` batches AOI signals into one packet per client per tick.

**Additions v1 lacked**

- Version negotiation by range (`min(client, current)`, reject below `MinSupported`); unknown TypeIds ignored and counted rather than fatal.
- Reconnect/resume: 16-byte resume key, 30-second grace, entities frozen, deferred `PeerLeftEvent`.
- Explicit authority rules in the contract, ownership-policy bits in the flags byte, and `HostChangedEvent` for host migration.
- `SetClientPrefsCommand` for hidden tabs and send-rate division.
- Input validation as a specified surface (finiteness, bounds, quantized ranges, kind allowlist, speed check) with per-client violation counters.
- `RejectCode.KindNotAllowed` (18).
- All control messages version-tolerant with explicit member ordering — retrofitting that later would itself have been a wire break.

**Structural**

- `NetId` is 16/16 instead of 20/12: slot bits beyond 16 are unusable under slot addressing, so they buy generations instead (65 536 reuses per slot).
- Naming convention: `…Command` for fire-and-forget C→S, `…Request`/`…Response` only for genuinely correlated pairs, `…Event` for S→C facts, `…Packet` for hand-packed hot payloads (freeing "frame" for its real meaning). "Remote event" became **signal**, matching pix3's own signals engine; TypeId range 128–191 is now signals.
- v1 support is deleted outright rather than maintained: `MinSupported = Current = 2`.

**Do not use MemoryPack's TypeScript generator** for the client codecs — it has an open nullable-float correctness bug. Hand-write the control codecs and gate CI on golden vectors produced by the C# side.

### v1 (2026-07) — never shipped

Initial contract: 20 messages, MemoryPack control plane, float32 hot plane, AOI + encode-once/memcpy-many, `slot | (generation << 20)` netIds. Superseded before any client spoke it.
