# Aetheria

A hardcore, 3D-isometric fantasy MMORPG (PvE + PvP) in C#, built server-authoritative from day one
for a **seamless** world. This repository is the server + networking core and a headless test client.
The Unity rendering client is added later (see [ROADMAP](docs/ROADMAP.md)).

> **Status: M0 — walking skeleton.** A fixed-timestep authoritative server simulates a continuous
> world; clients connect over UDP, send movement input, and receive per-client area-of-interest
> snapshots. Two clients can see each other move in real time and drop out of view when they leave
> each other's interest radius. This is the foundation everything else is built on.

## Why it's built this way

The hard part of an MMO is never the classes or the art — it's the *massively*. Two decisions here
protect against the usual failure mode (build a huge world, never ship):

- **The server is authoritative.** Clients send *intent*; the server decides what happens. This is
  non-negotiable for hardcore PvP — the client is never trusted. See
  [ADR-0001](docs/adr/0001-authoritative-server.md).
- **Interest management from the first commit.** The server simulates everything but only tells each
  client about entities near it, via a spatial grid. This is what lets one continuous world scale,
  and it's the same seam that later enables server meshing. See
  [ADR-0002](docs/adr/0002-interest-management.md).

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full picture, including the path from a
single seamless node to a meshed, truly-massive world.

## Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) or newer.
- Any editor: Rider, Visual Studio 2022 (17.13+ for `.slnx`), or VS Code + C# Dev Kit.

## Build, test, run

```bash
# from the repository root
dotnet build Aetheria.slnx -c Release      # compiles clean; warnings are errors

dotnet run --project tests/Aetheria.Tests -c Release   # runs the unit tests

# Terminal 1 — start the authoritative server (UDP 27015 by default)
dotnet run --project src/Aetheria.Server -c Release

# Terminal 2 and 3 — connect headless test clients
dotnet run --project src/Aetheria.Client.TestHarness -c Release -- --name Aria  --dirx 1  --seconds 20
dotnet run --project src/Aetheria.Client.TestHarness -c Release -- --name Borin --dirx -1 --seconds 20
```

You'll see each client report the entities inside its area of interest each second, and watch the
other player enter and leave that set as they move apart.

### Test client flags

| Flag        | Meaning                                  | Default     |
|-------------|------------------------------------------|-------------|
| `--host`    | Server host                              | `127.0.0.1` |
| `--port`    | Server UDP port                          | `27015`     |
| `--name`    | Display name sent in the handshake       | `tester`    |
| `--seconds` | How long to stay connected               | `8`         |
| `--dirx`    | Constant X movement intent (-1..1)       | `0`         |
| `--diry`    | Constant Y movement intent (-1..1)       | `0`         |

## Project layout

```
Aetheria.slnx
├── src/
│   ├── Aetheria.Shared/            # code shared by server & client
│   │   ├── Math/                   #   Vec2 (world-plane vector)
│   │   ├── Spatial/                #   SpatialGrid — interest management
│   │   ├── Protocol/               #   wire format: PacketReader/Writer, messages
│   │   └── Net/                    #   ITransport abstraction + raw-UDP implementation
│   ├── Aetheria.Server/            # headless authoritative server
│   │   ├── World/                  #   World, ServerEntity (the source of truth)
│   │   ├── GameServer.cs           #   network <-> world glue, handshake, snapshots
│   │   └── FixedStepLoop.cs        #   deterministic fixed-timestep driver
│   └── Aetheria.Client.TestHarness/# headless client for testing (no rendering)
└── tests/
    └── Aetheria.Tests/             # zero-dependency unit tests (grid, protocol, world)
```

## A note on dependencies

The skeleton has **zero external NuGet dependencies** — it builds and runs on the .NET SDK alone.
That is deliberate for a clean start ([ADR-0004](docs/adr/0004-zero-dependency-skeleton.md)); the
first roadmap tasks swap the hand-rolled pieces for battle-tested libraries (LiteNetLib for reliable
UDP, xUnit for tests) behind the interfaces already in place.

## License

TBD — add before any public release.
