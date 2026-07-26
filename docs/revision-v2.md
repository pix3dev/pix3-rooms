# Protocol v2 — revision decision record

Status: **accepted, pending application** · Date: 2026-07-26

This is the triaged outcome of a best-practice review of the v1 spec (Valve/Source, Quake 3, Fiedler's snapshot series, Overwatch's GDC netcode talk, Tribes/Halo:Reach interest management, Colyseus, Unity NGO/Mirror/FishNet/Netick, Godot, Photon, Rune, SpacetimeDB, plus browser-transport and .NET 10 runtime specifics). It records **what changes, what deliberately does not, and why**, so nothing here gets re-litigated later.

It is a separate file on purpose: the server modules are being implemented against v1 right now. When that lands green, this document is applied to [`protocol.md`](protocol.md) and [`architecture.md`](architecture.md) in **one coordinated pass**, and then deleted (its content lives on in the two specs plus their changelog).

## Verdict

The architecture holds. AOI + encode-once/memcpy-many + per-client known-set bitsets is the right shape for 600 players in one room, and nothing in the review argued for a different replication core (Netick publishes 250 objects → 250 clients encoded in <0.35 ms/core, so encoding was never going to be our bottleneck). Two wire-visible things must change before a client exists, and one of them is a correctness bug rather than a byte count.

**Flaw 1 — the known set assumes delivery that we do not have.** The server flips a client's "knows this entity" bit while composing a frame, but the per-connection send queue (inherited `DropOldest`, cap 256) can drop that frame afterwards. Position updates self-heal because they carry absolute values and are re-sent whenever dirty; **enter and removal records do not** — a dropped frame leaves that client with a permanent ghost entity or a permanently invisible one, and v1 has no recovery primitive at all. On WSS the lossy link is not the network, it is our own queue. Three researchers found this independently.

**Flaw 2 — the float32 hot layout has zero headroom.** 40 visible × ~26 B × 20 Hz × 600 clients ≈ 100–113 Mbit/s, i.e. 86–100% of both the per-client and the room budget *before* any spike. Worse, an AOI *radius* does not bound worst-case egress: 600 players stacked on one point all see each other. Quantizing now is free; after the first published game embeds a client bundle, it is a dual-encoder tax forever.

Everything else that the literature offers — ack bitfields, delta-from-acknowledged baselines, input prediction, server rewind, bit-packing, compression — is correctly absent from v1 and stays absent. **v2 is the first version that ever ships**, so v1 support is deleted outright rather than maintained.

---

## 1. Recovery model — adopt `Seq` + resync, reject ack/baseline machinery

- **`u16 Seq`** in every server→client hot frame, one monotonic counter per client, incremented only when a frame is actually emitted. Client rule: a gap (`seq != last + 1 mod 2¹⁶`) means desync — request a resync and ignore hot frames until the next snapshot. Cost: 2 B/frame ≈ 0.3 kbit/s.
- **`ResyncCommand`** (client→server, empty payload): server clears that client's known-set bitset and re-sends a full snapshot on the next tick through the existing continuation cursor. One primitive covers queue overflow, tab refocus, reconnect, and future datagram loss. A resync costs ~40 × 20 B ≈ 800 B.
- **Two-phase known-set commit**: bits flip only *after* the frame is successfully enqueued. `WriteDelta` records which bits it intends to set and the caller commits or rolls back. This is the actual fix for Flaw 1 — `Seq` and resync are the safety net, not the cure.
- **Send-queue lanes**: `FullMode.Wait` instead of `DropOldest` (which also leaks the rented buffer unless `itemDropped` is wired). Hot-lane enqueue failure → return the buffer, mark the client for resync. Control-lane failure → close the connection; control frames must never be dropped silently.
- **Rejected: Quake 3 style delta-from-acked-baseline and ack bitfields.** Their virtue is automatic recovery when the *transport* drops, which TCP does not. Keeping 32 known-set snapshots per client would cost ~10 MB per room and a replication rewrite. For the datagram era the answer is written into the spec now and implemented later: `Seq` gaps trigger resync, 1/32 of each client's known set is re-sent as full records every tick (whole AOI refreshed in 1.6 s), and an empty frame every 10 ticks keeps `Seq` advancing on quiet ticks.

## 2. Quantization — adopt, with the dirty-detection dividend

World bounds are declared per room (`WorldOriginX/Y`, `WorldSize`, default −2048/−2048/4096) and echoed in the welcome message.

| Field | Wire | Encoding | Precision |
|---|---|---|---|
| X, Y | `u16` | `round((v − origin) × 65535 / WorldSize)` | 1/16 unit at 4096 |
| Rot | `u8` | `round((rot mod 2π) / 2π × 256) & 0xFF` | 1.41° |
| Vx, Vy | `i16` | `round(v × 8)`, clamped | 0.125 u/s, ±4095 u/s |

Records stay **byte-aligned** — no bit-packing — because memcpy fan-out is worth more than the last 15%.

The **normative quantize-on-both-sides rule**: the quantized integers *are* the replicated values. The server stores dequantized-from-quantized floats as authoritative state; an owning client publishes quantized values and renders its own entity from the same dequantized values, so nobody chases divergence pops. The unexpected dividend: **dirty detection compares quantized integers, never floats**, which also kills the "float noise keeps an idle entity dirty forever" failure mode that v1 would have shipped with.

## 3. Projectiles leave the entity model

Thousands of bullets as replicated entities was the plan's biggest hidden cost. They are fully described by origin + direction + speed + spawn tick, so: **firing is a signal scoped to AOI peers, and every client simulates the bullet locally.** Hits are resolved by the owning client at Level 1 and announced as game events; the fabric never sees a bullet. Precedent is strong — Source's one-shot effects are *temporary entities* that never get an edict and never count against the networked-entity limit; Halo:Reach replicates ragdoll initial state only.

That moves the cost from bandwidth to per-message overhead (~200 fire events/s per client in heavy combat would be 200 extra socket sends), so signals get batched: one **`SignalBatchPacket`** per client per tick, assembled with the same encode-once/memcpy-many discipline, flushed alongside the delta. Worst case ~10 events/tick × 21 B ≈ 34 kbit/s and **zero** extra sends.

Consequence worth noting: with projectile churn gone, entity-slot generation pressure disappears — the concern that 12 generation bits would exhaust in ~90 minutes of bullet spawning no longer applies.

## 4. Hot layouts — v2

All little-endian, byte-aligned.

**`FullRecord` — 20 B** (was 31): `u32 NetId | u16 Kind | u32 OwnerId | u16 QX | u16 QY | u8 QRot | i16 QVx | i16 QVy | u8 Flags`

**`UpdateRecord` (S→C) — 3…13 B** (was 5…26): `u16 Slot | u8 Mask | masked fields in bit order`

**`OwnerUpdateRecord` (C→S) — 5…15 B**: same masked fields but keyed by `u32 NetId`, because the server needs the generation bits to reject a stale-slot mutation.

**Removal — 2 B**: `u16 Slot` (was 4).

Server→client traffic addresses entities by **slot**, not netId: the client's known set maps slot → netId, and on an ordered stream a removal always precedes any reuse of that slot (removals are processed first within a frame). This requires `MaxEntities ≤ 65535` (default 4096).

**Frame headers** gain `Seq`, and snapshots gain a `FrameFlags` byte whose bit 0 means **Final** — v1 gave a client no way to know a multi-frame snapshot was complete.

Mask bit meanings are unchanged. Velocity stays in the format and in the mask vocabulary but is **off the wire by default**: at 20 Hz, linear interpolation of 2D sprites does not need it (Fiedler needed velocity only for Hermite interpolation at 10 packets/s). A typical moving entity costs **8 B** (position + rotation).

**Budget after v2**: ~30 moving entities × 8 B + 13 B header ≈ 280 B/tick with framing → **45 kbit/s per client, ~27 Mbit/s per room** — 4× headroom. And the ceiling becomes a *guarantee* rather than a hope via three caps: `MaxBytesPerClientPerTick = 1100` (one MSS, and one future QUIC datagram), `MaxEntersPerTick = 24` with a carry cursor, and `MaxVisibleEntities = 64` chosen k-nearest by squared distance. Absolute worst case: 176 kbit/s per client, 105 Mbit/s per room.

## 5. Additions v1 simply lacked

- **Reconnect/resume**: a 16-byte resume key issued in the welcome message (regenerated every connect, per Colyseus's impersonation reasoning) and optionally presented in the handshake. Within a **30 s grace** the client keeps its id and its entities, which freeze in place; the peer-left notification is deferred. A failed resume silently degrades to a fresh join — no new error paths.
- **Version negotiation by range, not equality.** The client announces the highest version it speaks; the server runs the session at `min(client, current)` and rejects only below its minimum. **Unknown TypeId is ignored and counted, never fatal, in both directions.** This is what lets a game published six months ago keep working when the fabric adds messages — strict matching is right for a shipped game client, wrong for a platform hosting other people's bundles.
- **Authority made explicit for 3 bytes**: the welcome message carries the room's mode, and the spec states the rules — you simulate and publish what you own, `OwnerId == 0` is server-owned and read-only, and a server update for *your* entity is an authoritative correction to snap to. This makes Level-2 server validation a zero-byte, non-breaking upgrade instead of a protocol break.
- **Hidden-tab handling**: a client-preferences message with a `Hidden` flag and a send-rate divisor. Hidden means the server suspends that client's hot plane entirely and re-snapshots on un-hide. This matters more than it sounds: Chrome throttles timers to once per second in hidden tabs and once per *minute* after five minutes, and `requestAnimationFrame` stops outright — a backgrounded tab cannot drain a 20 Hz stream, it buffers it.
- **Input validation that a single packet could otherwise weaponize**: every inbound float checked finite and clamped to the world AABB (one NaN poisons the spatial hash — a one-packet room DoS), every quantized field range-checked, and a per-room allowlist of entity kinds (an unknown `kind` would otherwise fault every observer's scene code).
- **AOI hysteresis**: enter at the radius, exit at 1.25× it. At 600 players the arena edge is all boundary, and flapping pairs cost ~22 B/tick each.

## 6. Implementation decisions

- **Tick loop**: replace `PeriodicTimer` with a dedicated thread per room using absolute deadlines (`t0 + n × frequency / tickHz`), sleeping to ~2 ms short and spinning the tail, **skipping missed ticks rather than catching up**. Windows' default timer granularity is 15.625 ms — ±31% jitter on a 50 ms tick — and `PeriodicTimer` silently coalesces missed ticks. *My modification to the proposal:* the spin tail is conditional (coarse-granularity platforms, or rooms above a player threshold), because 64 idle rooms each burning 4% of a core to spin is a bad trade; production is Linux, where plain sleeping is ~1 ms accurate.
- **GC**: Server GC with **DATAS explicitly off** (`GarbageCollectionAdaptationMode = 0`) — it is enabled by default with Server GC since .NET 9, ramps heap count reactively and schedules full compacting collections, with 1.16–1.69× regressions reported on latency-sensitive workloads. *Already applied* to `Directory.Build.props`.
- **Transport hardening**: never enable `permessage-deflate` (64–316 KiB of zlib context per connection, and context takeover breaks datagram portability) with a handshake test asserting no `Sec-WebSocket-Extensions` in the 101; pin `/ws` to HTTP/1.1 (browsers negotiate RFC 8441 WebSockets-over-HTTP/2 by default — pure overhead for one long-lived binary socket); `KeepAliveInterval`/`KeepAliveTimeout` at 15 s so protocol-level pings survive throttled tabs and dead mobile sockets are detected; set `MaxConcurrentUpgradedConnections` explicitly, since Kestrel's default is unlimited.
- **Pre-auth gate**: 2 s handshake timeout, exactly one frame ≤2 KiB accepted before authentication, no client id or room state allocated until then, an Origin allowlist (cross-site WebSocket hijacking), a new-connection token bucket per IP, and a global pre-auth connection cap. The token stays in the first frame — never in the query string.
- **AOI focus comes from server state, not a client claim**: bind a subscriber's focus to an owned entity's *server-side* position each tick instead of accepting free coordinates. Free-position focus survives for spectators, speed-clamped. This deletes the "teleport my focus every tick to force enormous enter-sets and amplify to N peers" exploit at its source; the enter cap bounds whatever remains.
- **Serialization**: all control messages become version-tolerant with explicit member ordering **now**, because retrofitting that later is itself a wire break. **Do not use MemoryPack's TypeScript generator** — it has an open nullable-float correctness bug — hand-write the ~14 TS control codecs and gate CI on golden vectors produced by the C# side. (This also removes the codegen fragility that counted against the .NET stack in the first place.)
- **Verification**: a zero-allocation CI gate asserting `GC.GetAllocatedBytesForCurrentThread()` is unchanged across 10 000 simulated ticks; a debug-only generation-stamped buffer pool that fills returned buffers with `0xDD` and asserts on use-after-return; and three histograms — tick start jitter, tick body time, and enqueue-to-socket-write, the last being the one players actually feel.
- **Quota table** (defaults): 60 msg/s and 8 KiB/s inbound per connection; 4 KiB inbound payload cap; 8 entity updates per frame; 240 spawns/min; 64 entities per owner; signals 20/s to server, 10/s to AOI peers, **2/s to all peers** (a 600× amplifier); chat 10/min ≤240 chars; cold props ≤512 B at 2/s per entity; 8 connections per IP plus 4 pre-auth; teleport bits 12/min soft. Violations increment per-client counters (`ownership, speed, mask, nan, kind, quota, focusClamp, teleport`) exposed through the admin API — **build the dataset now, the detector later**. The Level-1 speed check (`|Δpos| ≤ maxSpeed·Δt·1.25`, counted not enforced) goes in behind the same seam: it *is* the Level-2 validator, written early.

## 7. Naming convention — applied in the same pass

v1 inherited WsCore's `Request`/`Event` suffixes, which have four problems: `Request` implies a response that only *one* message pair actually has; "Event" means both "server→client message" and "user's game event routed over the network"; `RemoteEventBroadcast` is the sole `Broadcast` suffix in the codebase; and `Frame` means WebSocket frame, render frame, *and* hot payload at once.

The convention: `…Command` for fire-and-forget client→server (verb first), `…Request`/`…Response` only for genuinely correlated pairs, `…Event` for server→client facts in past tense, `…Packet` for hand-packed hot payloads (the suffix warns "do not MemoryPack this" and frees `Frame` for its real meaning). The networked-event concept is renamed **Signal**, matching pix3's own signals engine — one word, one concept; the runtime API stays Roblox-flavoured (`net.on` / `net.emit`) and players never see wire names. Prior art: Unity's `[Command]`/`[ClientRpc]` split, Quake's `clc_`/`svc_` sender prefixes, and the CQRS command-versus-event distinction.

| v1 | v2 |
|---|---|
| `HelloRequest` | `HelloCommand` |
| `PingRequest` | `PingCommand` |
| `ChatMessageRequest` | `SendChatCommand` |
| `LeaveRequest` | `LeaveCommand` |
| `SetRoomVarRequest` | `SetRoomVarCommand` |
| `EntitySpawnRequest` / `EntitySpawnAckEvent` | `SpawnEntityRequest` / `SpawnEntityResponse` |
| `EntityDespawnRequest` | `DespawnEntityCommand` |
| `SetEntityColdPropsRequest` | `SetEntityPropsCommand` |
| `EntityColdPropsEvent` | `EntityPropsChangedEvent` |
| `RemoteEventRequest` / `RemoteEventBroadcast` | `EmitSignalCommand` / `SignalEvent` |
| `RejectEvent` | `RejectedEvent` |
| `RoomVarsEvent` | `RoomVarsChangedEvent` |
| `EntityUpdateFrame` / `SnapshotFrame` / `DeltaFrame` | `EntityUpdatePacket` / `SnapshotPacket` / `DeltaPacket` |
| *(new)* | `ResyncCommand`, `SetClientPrefsCommand`, `SignalBatchPacket` |

Unchanged: `WelcomeEvent`, `PongEvent`, `PeerJoinedEvent`, `PeerLeftEvent`, `RoomInfoEvent`, `ChatMessageEvent` (no longer tautological now that the command is a verb). Also `RemoteEventTarget` → `SignalTarget`, and TypeId range 128–191 → signals. The discipline the contract agent already kept stays: a `MessageTypeIds` constant is spelled exactly like its class, so one grep finds a message's whole path.

## 8. Two further modifications of mine

1. **`NetId` becomes 16/16 instead of 20/12.** Server→client records address entities by `u16 Slot`, which caps `MaxEntities` at 65535 anyway, so slot bits beyond 16 are unusable — spending them on generations instead gives 65 536 reuses per slot rather than 4 096, for free. The existing implementation's rule ("a slot whose generation is exhausted is retired, never wrapped") is right and stays; this just makes retirement practically unreachable.
2. **Entity ownership policy joins the flags byte** (from an external plan review, and it is wire-affecting so it must land in this pass): two bits distinguishing `owned` (dies with its owner) from `shared`/`transferable` (reassigned to the new host on owner exit). Without it `RemoveOwner` despawns a departing host's pickups, and Level-1 host migration is impossible — which kills every public "Play with friends" session whose creator backgrounds their phone. Two bits now, no wire break later; the migration logic itself (`HostChangedEvent`, promote longest-present member, reassign) is Rooms-side work in Phase 1–2.
3. **`Kind` gets a documented source of truth.** It indexes the build's prefab table, which means the manifest emitted by the pix3 exporter and the room's allowlist must agree. Until the exporter emits that table, dev rooms allow any kind and production rooms require an explicit list.

## 9. Rejected, so it stays rejected

Ack bitfields and acked baselines (TCP already orders and retransmits; resync covers our own queue). Replicated projectile entities (churn, spawn-ack latency, table/AOI pressure). Per-kind hot schemas (no consumer once projectiles are events). Fiedler's priority accumulator (the k-nearest cap plus a byte budget covers the dogpile case more simply — revisit only if load tests show the cap biting in normal play). Bit-packing, smallest-three quaternions, varints, range coding, any stream compression (breaks memcpy fan-out, double-digit CPU at 12 k frames/s, and float mantissas barely compress). Colyseus schema / FlatBuffers / Cap'n Proto / Bebop on the hot plane (JS-only or larger on the wire). Orleans / MagicOnion / Nakama / SpacetimeDB / Rune / Croquet as the fabric (actor overhead, gRPC framing, relay-not-AOI, subscription-SQL AOI, or full-world client simulation — none survives 600-in-one-room on mobile browsers). WebRTC DataChannel (a second network stack owned forever for what WebTransport will hand us). Input prediction, reconciliation, time dilation and server rewind (Level-1 owners simulate locally with zero latency; there is no server hit detection to rewind — these are Level-2/3 items). Stripping the client teleport bit at Level 1 (respawns are legitimate under client authority; quota it now, strip it at Level 2). NativeAOT, ReadyToRun, `System.IO.Pipelines`, GC heap affinity, `GCHeapHardLimitPercent`, `timeBeginPeriod(1)`, per-room slab allocators (each loses to the JIT steady state, adds complexity for ~220-byte frames, or fights the tick-loop fix). HTTP/2 WebSockets (actively pinned off).

Deferred rather than rejected: single-use `jti` cache and ES256 + JWKS (do it when identity becomes a separate deployable), per-room deny-sets.

## 10. Datagram portability contract

Zero code now, but every choice above respects it, so moving the hot plane to WebTransport datagrams later is a transport change rather than a protocol change: hot frames are ≤1100 B and self-contained, every hot frame carries `Seq`, `ResyncCommand` exists, the round-robin refresh has a stubbed seam, length prefixing belongs to the transport (streams add a length, datagrams do not) so the layouts in `protocol.md` never change, and there is no compression context and no cross-frame state except the known set — which resync can rebuild. Kestrel's own WebTransport datagram support is .NET-11-era anyway.

## 11. Application order

1. Let the in-flight build reach green with tests (v1 semantics) — the modules are 80% orthogonal to these changes.
2. Apply this document to `protocol.md` + `architecture.md`, bump `ProtocolVersion.Current = 2` with `MinSupported = 2`, and delete this file.
3. Protocol project: quantized state + `Quantizer`, v2 record/frame codecs, three new messages, version-tolerant attributes, regenerated golden vectors, the rename. (~1.5 days)
4. Replication: quantized SoA columns and integer dirty compare, slot-addressed writers, per-client `Seq`, **two-phase known-set commit**, enter cursor, byte-budget fill loop, k-nearest cap, hysteresis, focus binding. The core design survives; the commit-ordering change is the one that alters `WriteDelta`'s contract, so it lands first. (~2–3 days)
5. Net/Auth: queue lanes and `Wait` mode, pre-auth gate, transport hardening, auth tightening. (~2 days)
6. Rooms: tick-loop rewrite, signal batching, resume grace, hidden handling. (~2.5 days)
7. Tests/LoadGen: regenerate vectors, add resync/resume/hidden scenarios, self-measured wake jitter. (~1.5 days)

Roughly **10–12 working days** of server-side work, then the client side (~3–5 days in the pix3 runtime repo): worker-hosted socket pump, interpolation buffer, resync/reconnect/backoff, visibility handling, hand-written TS codecs with shared golden vectors.
