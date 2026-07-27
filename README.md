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

## Deploy

Production lives at **`rooms.pix3.dev`** — a sibling of `cloud.pix3.dev` (identity, projects, collaboration) and `editor.pix3.dev`. Point an `A`/`AAAA` record at the host before the first deploy; certbot needs it resolving.

Two supported shapes, both driven from `deploy/`:

| | systemd (production today) | Docker Compose (dev stand, future orchestration) |
|---|---|---|
| Artifact | self-contained `linux-x64` publish, no .NET on the host | `deploy/Dockerfile`, non-root `app` user |
| Delivery | `.github/workflows/deploy.yml` — SSH, `releases/<sha>` + `current` symlink, health-gated with automatic rollback | `docker compose -f deploy/docker-compose.yml up -d --build` |
| Budget | `CPUQuota`/`MemoryMax` in the unit | `deploy.resources` in the compose file |
| TLS | host nginx + certbot, `deploy/nginx-host.conf` | nginx + certbot containers, `deploy/nginx.conf` |

They are not redundant: the image is what CI builds to prove the publish is whole and what a second host would run, the unit is what serves players. Restarting drops every room either way — rooms are in-memory and there is no drain protocol yet, so a deploy is a disruption in both shapes.

Two settings are easy to miss and neither fails loudly:

- **`Rooms__Server__TrustForwardedHeaders=true` is mandatory behind the proxy.** Every socket arrives from nginx, so without it the per-IP quotas see one address and the ninth player in a room is rejected by `MaxConnectionsPerIp`. Enable it *only* while nothing but nginx can reach the app port.
- **`AllowedOrigins` and `Defaults:AllowedKinds` must be non-empty**, or startup refuses to run in Production. Arrays bind by index: `Rooms__Auth__AllowedOrigins__0=…`.

### systemd: one-time host preparation

```bash
# 1. Service account (writes nothing, owns nothing) and the deploy tree.
sudo useradd --system --no-create-home --shell /usr/sbin/nologin pix3rooms
sudo mkdir -p /opt/pix3-rooms/{releases,shared}
sudo chown -R deploy:deploy /opt/pix3-rooms

# 2. Configuration. Fill in the secrets from pix3-collab-server, then lock it down.
sudo cp deploy/systemd/rooms.env.example /opt/pix3-rooms/shared/rooms.env
sudo chown deploy:pix3rooms /opt/pix3-rooms/shared/rooms.env
sudo chmod 640 /opt/pix3-rooms/shared/rooms.env

# 3. The unit.
sudo cp deploy/systemd/pix3-rooms.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable pix3-rooms

# 4. Let CI restart it, and read its journal, without a password.
echo 'deploy ALL=(root) NOPASSWD: /usr/bin/systemctl restart pix3-rooms, /usr/bin/systemctl status pix3-rooms' \
  | sudo tee /etc/sudoers.d/pix3-rooms
sudo usermod -aG adm deploy

# 5. TLS, then the reverse proxy — in that order. Issue with --webroot (the default_server on
#    :80 already serves /var/www/html), because the site config below references cert paths that
#    do not exist yet and `nginx -t` would fail on them.
sudo certbot certonly --webroot -w /var/www/html -d rooms.pix3.dev
sudo cp deploy/nginx-host.conf /etc/nginx/sites-available/rooms.pix3.dev
sudo sed -i "s/DOMAIN_PLACEHOLDER/rooms.pix3.dev/g" /etc/nginx/sites-available/rooms.pix3.dev
sudo ln -s /etc/nginx/sites-available/rooms.pix3.dev /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
```

**All five steps are done on the production host, and a first release is running there** — `https://rooms.pix3.dev/health` answers `200`, room create/list/destroy round-trips through the public URL, and `/ws` completes a `101` (without `Sec-WebSocket-Extensions`) before the pre-auth deadline closes an unauthenticated socket. The deploy account is `deploy-user`, the tree is `/opt/pix3-rooms`, and the service token lives only in `shared/rooms.env` on the host. The one remaining task is putting `ROOMS_DEPLOY_*` into the repository's `production` environment; the CI key's private half is at `/root/rooms-deploy-key` on the host and its public half is already in `deploy-user`'s `authorized_keys`.

Sizing there is deliberately *not* the 600-player target: `MaxRooms=8`, `MaxTotalConnections=64`, and per-IP caps raised to 32/16 because a 20-player test is often many tabs behind one address. A room this box cannot serve should be refused, not accepted and starved.

Every http-level name in `nginx-host.conf` is prefixed `rooms_` because the host is shared: an unprefixed `map $http_upgrade $connection_upgrade` colliding with a neighbouring site file is a config nginx refuses to start on.

**The production host today is `cloud.pix3.dev`'s box — one vCPU, 3.9 GB, also serving the Node collab server, `llm.gritsenko.biz` and `stat.pix2d.com`.** That is fine for a demo room and *cannot* meet this repo's stated target: a 600-player room is budgeted at roughly one core with a hard 20 Hz deadline, and here that core is shared with everything else. Expect `RoomStats.TickJitterMsP99` — the metric that exists precisely to catch this — to be the first thing that degrades under load. Hence `CPUWeight=800` rather than `CPUQuota` in the unit: on a contended single core the server needs to *win* the core, not be capped on it. A real load test needs its own box.

TLS is already provisioned on that host (`rooms.pix3.dev`, webroot-authenticated, auto-renewing). Certbot renewal reloads nginx through `/etc/letsencrypt/renewal-hooks/deploy/reload-nginx.sh` — webroot certs have no installer plugin, so without that hook nginx serves the expired certificate out of memory until something unrelated reloads it.

GitHub secrets for `deploy.yml` (environment `production`): `ROOMS_DEPLOY_USER`, `ROOMS_DEPLOY_SSH_KEY`, `ROOMS_DEPLOY_PATH` (`/opt/pix3-rooms`), `ROOMS_DEPLOY_PORT`.

```bash
# Operating it
sudo systemctl status pix3-rooms
journalctl -u pix3-rooms -f
ln -sfn /opt/pix3-rooms/releases/<sha> /opt/pix3-rooms/current && sudo systemctl restart pix3-rooms  # manual rollback
```

**Known gap.** systemd restarts the process when it exits, but it cannot probe `/health` — a server that is alive yet wedged stays up. The Docker shape has a `HEALTHCHECK`; the systemd shape does not. The fix is `UseSystemd()` plus `Type=notify` and `WatchdogSec=`, which needs the `Microsoft.Extensions.Hosting.Systemd` package (a dependency addition, and dependencies here are frozen — see `AGENTS.md`). Until then, external monitoring on `https://rooms.pix3.dev/health` is the liveness check.

The artifact is ~35 MB gzipped because it carries the runtime. Dropping `--self-contained true` from the workflow shrinks it to about a megabyte at the cost of pinning the host to an apt-installed .NET 10 runtime — a deliberate trade, not an oversight.

## Status

**Phase 0 complete; Phase 1 starting on the client side.** The server runs: composition root wired (options binding with startup validation, DI, `/ws` + admin REST + `/health` + `/metrics`, graceful shutdown), room lifecycle with per-room tick loops and TTL eviction, versioned handshake with JWT room tokens, quotas and rate limits, generic entity replication with spatial-hash AOI and encode-once fan-out, room-scoped chat and room variables, hand-rolled Prometheus metrics. Protocol v2 is implemented throughout and is what [`docs/protocol.md`](docs/protocol.md) describes. `dotnet test` runs **419** tests, and production is live at `rooms.pix3.dev`.

**The wire contract is pinned from both sides.** [`docs/protocol-vectors.json`](docs/protocol-vectors.json) holds byte-exact golden vectors — quantization, every hot-plane packet, every control-message shape — derived by hand from `docs/protocol.md` rather than captured from a codec. `tests/Pix3.Rooms.Tests/Protocol/GoldenVectorFileTests.cs` checks this server against it, and the hand-written TypeScript client in the pix3 repo (`packages/pix3-runtime/src/net/protocol/`) checks itself against a byte-identical copy. A `diff` between the two files is what proves the implementations are in sync.

Not yet: the runtime's `NetworkService` and replicated-transform components, the editor's "Play Online" flow, Level-2 server-side rules (movement validation, match flow, score), Level-3 user server scripts (headless `@pix3/runtime` in sandboxed workers), multi-node fabric, WebTransport. See the plan for sequencing.

## Lineage

The predecessor experiment [`WsCore`](https://github.com/gritsenko/WsCore) informed the socket layer (frame reassembly, bounded per-connection send queues, `[TypeId][payload]` framing). This repo is a clean start: rooms are genuinely isolated units of state and scheduling, authentication is mandatory, and AOI is part of the core rather than a claim in a README.
