# Contributing to Pix3 Rooms

Read the licensing section before opening a pull request — this repository is
licensed in two parts, and which one applies depends on the files you touch.

## Licensing model

| Path | Licence |
|---|---|
| `src/Pix3.Rooms.Protocol/` — the wire contract | Apache-2.0 |
| `tools/Pix3.Rooms.LoadGen/` — reference bot client | Apache-2.0 |
| `docs/protocol.md`, `docs/protocol-vectors.json` | Apache-2.0 |
| `src/Pix3.Rooms.Server/` — the Room Fabric server | Proprietary |
| `tests/`, `deploy/` | Proprietary |

The protocol is open on purpose. It is an interface, not the product: a complete
independent implementation already ships under Apache-2.0 in `@pix3/runtime`,
sharing the same golden vectors. Implementing this protocol — including in a
competing server — is expressly allowed. What is sold is *this* server's room
lifecycle, AOI replication, quotas, multi-tenancy and operations.

## Contributor Licence Agreement

Pull requests need a signed CLA; the bot will link it on your first PR.

A DCO would be enough for a project that is permissively licensed throughout.
This one is not — contributions may end up in a commercially licensed component,
and that needs an explicit grant from you, which a DCO does not provide.

You keep the copyright in your contribution; you grant the project a perpetual,
worldwide licence to use it, including under commercial terms. You also confirm
you have the right to grant that — note that in many jurisdictions code written
by an employee belongs to the employer by default, regardless of whose time or
hardware it was written on.

## Before you start

`AGENTS.md` is the binding rule set for code in this repository — read it first.
For protocol changes read [`docs/protocol.md`](docs/protocol.md) and
[`docs/architecture.md`](docs/architecture.md) as well.

Two rules worth repeating here because they are easy to get wrong:

- **`ProtocolVersion` and `<Version>` are unrelated.** The first moves only when
  the bytes change; the second is the pix3 platform version. Never bump one
  because the other moved.
- **A protocol change is a two-repository change.** The golden vectors in
  `docs/protocol-vectors.json` are byte-identical to
  `packages/pix3-runtime/src/net/protocol/fixtures/protocol-vectors.json` in the
  pix3 repository. Changing the wire format here without updating the client
  there breaks every game.

## Development

Requires the **.NET 10 SDK**.

```bash
dotnet build
dotnet test
dotnet run --project src/Pix3.Rooms.Server
```

## Dependencies

The runtime dependency surface is two MIT packages, and it should stay small: a
gateway terminating untrusted sockets is the wrong place to grow a dependency
tree. Every addition is a licence decision as well as a security one.

- **Permissive only** (MIT, BSD, Apache-2.0). No GPL/LGPL/AGPL in shipped code.
- Verify the licence from the package's own `.nuspec`, not from memory or from
  a badge.
- Update `THIRD-PARTY-NOTICES.md` in the same pull request.
