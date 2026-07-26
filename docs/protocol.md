# Pix3 Rooms wire protocol — v1

Authoritative spec. `Pix3.Rooms.Protocol` implements exactly this; clients (pix3 runtime, LoadGen) speak exactly this. Any change to a byte layout is a **protocol version bump**.

## Framing

Every WebSocket **binary** frame is one message:

```
[u8 TypeId][payload …]
```

Text frames are **rejected** (close 4007). All integers are **little-endian**. Floats are IEEE-754 `float32`.

Payloads come in two flavours:

- **Control plane** — MemoryPack-serialized classes (`[MemoryPackable]`). Low rate, schema-evolvable, readable.
- **Hot plane** (`TypeId` 67/68/69) — **hand-packed** fixed layouts, described below. These are on the 600-players-per-room path: they must be written with zero allocations and support *encode-once, memcpy-many* fan-out. Never MemoryPack these.

## Version handshake

`ProtocolVersion.Current = 1`.

The **first** frame a client sends must be `HelloRequest` (TypeId 1). Anything else → `RejectEvent{BadRequest}` + close 4007. If `HelloRequest.ProtocolVersion != ProtocolVersion.Current` → `RejectEvent{ProtocolVersionMismatch}` + close 4001. No exceptions: the mismatch must produce a typed, human-readable rejection, never a decoder error.

Successful handshake: `HelloRequest` → validate token → resolve room → join → `WelcomeEvent`, then `RoomVarsEvent`, then one `SnapshotEvent`, then `PeerJoinedEvent` fan-out to the others.

## TypeId allocation

Ranges are reserved; do not allocate outside them.

| Range | Purpose |
|---|---|
| 0–63 | Core: handshake, session, chat, room vars |
| 64–127 | State sync (entities) |
| 128–191 | Remote events / RPC |
| 192–255 | Reserved for app/game-specific extensions |

### Core (0–63)

| Id | Name | Dir | Fields |
|---|---|---|---|
| 1 | `HelloRequest` | C→S | `ushort ProtocolVersion`, `string Token`, `string RoomId`, `string DisplayName`, `ushort Capabilities` |
| 2 | `WelcomeEvent` | S→C | `uint ClientId`, `string RoomId`, `byte TickHz`, `long ServerTimeMs`, `uint ServerTick`, `float AoiRadius`, `ushort MaxPlayers`, `ushort ProtocolVersion` |
| 3 | `RejectEvent` | S→C | `ushort Code` (`RejectCode`), `string Message` |
| 4 | `PingRequest` | C→S | `long ClientTimeMs` |
| 5 | `PongEvent` | S→C | `long ClientTimeMs`, `long ServerTimeMs`, `uint ServerTick` |
| 6 | `PeerJoinedEvent` | S→C | `uint ClientId`, `string DisplayName` |
| 7 | `PeerLeftEvent` | S→C | `uint ClientId`, `byte Reason` (`LeaveReason`) |
| 8 | `RoomInfoEvent` | S→C | `ushort PlayerCount`, `ushort EntityCount`, `uint ServerTick` — sent ~1 Hz |
| 9 | `ChatMessageRequest` | C→S | `string Text` |
| 10 | `ChatMessageEvent` | S→C | `uint ClientId`, `string Text` |
| 11 | `LeaveRequest` | C→S | *(empty)* |
| 12 | `SetRoomVarRequest` | C→S | `string Key`, `byte[] Value` |
| 13 | `RoomVarsEvent` | S→C | `string[] Keys`, `byte[][] Values` — full set on join, changed subset afterwards |

### State (64–127)

| Id | Name | Dir | Encoding |
|---|---|---|---|
| 64 | `EntitySpawnRequest` | C→S | MemoryPack: `uint RequestId`, `ushort Kind`, `float X`, `float Y`, `float Rot`, `byte[]? ColdProps` |
| 65 | `EntitySpawnAckEvent` | S→C | MemoryPack: `uint RequestId`, `uint NetId`, `ushort RejectCode` (0 = ok) |
| 66 | `EntityDespawnRequest` | C→S | MemoryPack: `uint NetId` |
| 67 | `EntityUpdateFrame` | C→S | **hand-packed** (below) |
| 68 | `SnapshotFrame` | S→C | **hand-packed** (below) |
| 69 | `DeltaFrame` | S→C | **hand-packed** (below) |
| 70 | `SetEntityColdPropsRequest` | C→S | MemoryPack: `uint NetId`, `byte[] Json` |
| 71 | `EntityColdPropsEvent` | S→C | MemoryPack: `uint NetId`, `byte[] Json` |

### Remote events (128–191)

| Id | Name | Dir | Fields |
|---|---|---|---|
| 128 | `RemoteEventRequest` | C→S | `string Name`, `byte Target` (`RemoteEventTarget`), `uint TargetClientId`, `byte[] Payload` |
| 129 | `RemoteEventBroadcast` | S→C | `uint SenderClientId`, `string Name`, `byte[] Payload` |

## Hot plane layouts

### `EntityWireState`

The canonical mutable entity state on the wire:

```
f32 X, f32 Y, f32 Rot, f32 Vx, f32 Vy, u8 Flags   (+ u16 Kind, u32 OwnerId in full records)
```

### Delta mask bits (`DeltaMask`)

| Bit | Name | Meaning |
|---|---|---|
| 0x01 | `X` | `f32 X` present |
| 0x02 | `Y` | `f32 Y` present |
| 0x04 | `Rot` | `f32 Rot` present |
| 0x08 | `Vx` | `f32 Vx` present |
| 0x10 | `Vy` | `f32 Vy` present |
| 0x20 | `Flags` | `u8 Flags` present |
| 0x40 | `ColdDirty` | cold props changed; client should expect `EntityColdPropsEvent` |
| 0x80 | `Teleport` | discontinuity — receiver must snap, not interpolate |

`0x40`/`0x80` carry no payload bytes. Client→server masks are limited to `0x3F | 0x80`.

### `FullRecord` — 31 bytes, fixed

```
u32 NetId
u16 Kind
u32 OwnerId
f32 X, f32 Y, f32 Rot, f32 Vx, f32 Vy
u8  Flags
```

### `DeltaRecord` — variable, 5…26 bytes

```
u32 NetId
u8  Mask
(f32 X)(f32 Y)(f32 Rot)(f32 Vx)(f32 Vy)(u8 Flags)   — present per Mask, in bit order
```

### `SnapshotFrame` (68, S→C)

```
u8  TypeId = 68
u32 ServerTick
u16 Count
FullRecord × Count
```

Sent once right after `WelcomeEvent` (entities currently inside the joiner's AOI). Large snapshots are split across multiple frames — each frame is self-contained.

### `DeltaFrame` (69, S→C)

```
u8  TypeId = 69
u32 ServerTick
u16 RemovedCount ; u32 NetId × RemovedCount     — despawned OR left AOI
u16 EnterCount   ; FullRecord × EnterCount      — entered AOI
u16 UpdateCount  ; DeltaRecord × UpdateCount    — already-known entities that changed
```

A tick with nothing for a given client produces **no frame at all**.

### `EntityUpdateFrame` (67, C→S)

```
u8  TypeId = 67
u32 ClientTick
u8  Count
DeltaRecord × Count
```

Server rules: every `NetId` must be owned by the sender (else the record is dropped and a violation counter incremented), `Count` is quota-capped, and the server stamps its own tick — client tick is advisory only (telemetry/ordering).

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
| 15 | `EntityLimitReached` | — (spawn ack only) |
| 16 | `NotEntityOwner` | — (spawn/despawn ack only) |
| 17 | `InternalError` | 4000 |

## Leave reasons (`LeaveReason : byte`)

`0 Disconnected`, `1 LeftVoluntarily`, `2 Kicked`, `3 Timeout`, `4 RoomClosed`, `5 Error`.

## Remote event targets (`RemoteEventTarget : byte`)

`0 Server`, `1 AllPeers`, `2 SinglePeer`, `3 AoiPeers`.

## Invariants a conforming implementation must uphold

1. One entity update path: a client may only mutate entities it owns (L1 client authority).
2. `NetId` is opaque to clients: `slot | (generation << 20)`. Never reuse a `(slot, generation)` pair within a room's lifetime.
3. Servers never trust client-supplied ticks, ids, or masks without validation.
4. Frames are ≤ `MaxPayloadBytes` (default 16 KiB) in both directions; the server splits its own oversized snapshots rather than exceeding it.
5. Every close is preceded by a `RejectEvent` when the reason is known, so the client can show a real message.
