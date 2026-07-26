# pix3-rooms — the Room Fabric for pix3

Multi-tenant .NET 10 WebSocket server that hosts multiplayer game rooms for scenes authored in the [pix3](../pix3) editor. Press **Play Online** in the editor, share a link or QR code, and a friend joins from a phone browser — that is what this server exists to make possible.

It is deliberately **game-agnostic**: it terminates sockets, validates identity, owns room lifecycle and quotas, and replicates generic entity state with area-of-interest filtering. It knows nothing about pix3 nodes, components or prefabs — game semantics live in `@pix3/runtime` on the client, and later in Level-3 server workers behind this gateway.

Design target: **600 concurrent players in a single room** (2D top-down shooter) without lag, on a budget of roughly one CPU core and ~115 Mbit/s per room.

- Product plan and phasing: [`pix3/.plans/multiplayer-platform.md`](../pix3/.plans/multiplayer-platform.md)
- Architecture and module contract: [`docs/architecture.md`](docs/architecture.md)
- Wire protocol (byte-exact): [`docs/protocol.md`](docs/protocol.md)
- Rules for anyone (human or agent) writing code here: [`AGENTS.md`](AGENTS.md)

## Layout

```
src/Pix3.Rooms.Protocol/   wire contract: MemoryPack control messages + hand-packed hot codecs
src/Pix3.Rooms.Server/     the server: Net, Auth, Rooms, Replication, Admin, Observability
tests/Pix3.Rooms.Tests/    xUnit: golden wire vectors, AOI correctness, rooms, auth, quotas, e2e
tools/Pix3.Rooms.LoadGen/  headless load generator (N bot clients, bandwidth/RTT report)
docs/                      architecture + protocol
```

Requires the **.NET 10 SDK**. The solution uses the new `.slnx` format.

## Run

```bash
dotnet run --project src/Pix3.Rooms.Server            # Development: Auth:Mode=Insecure, dev tokens
dotnet build Pix3.Rooms.slnx
dotnet test  Pix3.Rooms.slnx
```

Development mode accepts `dev:<subject>:<roomId>` tokens so the editor and the load generator can connect without the pix3 cloud. It refuses to start in Production — real deployments validate HS256 JWTs minted by `pix3-collab-server` (`aud: pix3-rooms`, `iss: pix3-cloud`, a `roomId` claim, short expiry).

### Create a room and connect

```bash
# 1. create (service token from Rooms:Auth:ServiceToken; dev default below)
curl -X POST http://localhost:5011/admin/rooms \
     -H "X-Service-Token: dev-service-token" -H "Content-Type: application/json" \
     -d '{"roomId":"demo-1","projectId":"demo","maxPlayers":64,"tickHz":20,"aoiRadius":1200}'

# 2. clients connect to ws://localhost:5011/ws?room=demo-1 and send HelloCommand{roomId, token}
```

### Load test

Planned interface of `tools/Pix3.Rooms.LoadGen` (still a stub — see [Status](#status)):

```bash
dotnet run --project tools/Pix3.Rooms.LoadGen -- \
  --admin http://localhost:5011 --service-token dev-service-token \
  --room auto --clients 600 --seconds 60 --input-hz 20 --json loadgen-600.json
```

## Endpoints

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /ws?room=<id>` | room JWT in `HelloCommand` | game socket (binary frames only) |
| `POST /admin/rooms` | `X-Service-Token` | create a room |
| `GET /admin/rooms`, `GET /admin/rooms/{id}` | `X-Service-Token` | list / inspect |
| `DELETE /admin/rooms/{id}` | `X-Service-Token` | destroy |
| `GET /health` | none | liveness |
| `GET /metrics` | optional service token | Prometheus text |

## Status

**Phase 0, mid-flight.** The server runs: composition root wired (options binding with startup validation, DI, `/ws` + admin REST + `/health` + `/metrics`, graceful shutdown), room lifecycle with per-room tick loops and TTL eviction, versioned handshake with JWT room tokens, quotas and rate limits, generic entity replication with spatial-hash AOI and encode-once fan-out, room-scoped chat and room variables, hand-rolled Prometheus metrics.

**In progress: the protocol v2 code pass.** [`docs/protocol.md`](docs/protocol.md) and [`docs/architecture.md`](docs/architecture.md) are already v2 and are the authority; the modules still implement v1 layouts and message names. Order of work: Protocol (quantization, slot-addressed records, renames) → Replication (two-phase known-set commit, `Seq`, caps, hysteresis, focus binding) → Net/Rooms (queue lanes, dedicated tick thread, pre-auth gate, resume grace, host migration) → tests + LoadGen.

Not yet: golden-vector and behaviour tests, a real load generator, Level-2 server-side rules (movement validation, match flow, score), Level-3 user server scripts (headless `@pix3/runtime` in sandboxed workers), multi-node fabric, WebTransport. See the plan for sequencing.

## Lineage

The predecessor experiment [`WsCore`](https://github.com/gritsenko/WsCore) informed the socket layer (frame reassembly, bounded per-connection send queues, `[TypeId][payload]` framing). This repo is a clean start: rooms are genuinely isolated units of state and scheduling, authentication is mandatory, and AOI is part of the core rather than a claim in a README.
