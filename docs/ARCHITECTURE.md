# Architecture

This document explains how Aetheria's server core is put together and, just as importantly, how it
is meant to grow. It is the map; the code is the territory.

## Design goals

1. **Authoritative and cheat-resistant.** For hardcore PvP the server must own the truth. Clients
   send input; the server decides outcomes. Nothing about a client's position, damage, or loot is
   ever taken on the client's word.
2. **Seamless world.** No loading screens between zones — one continuous space. The player should
   never see a seam.
3. **Scalable by construction.** "Seamless" and "massive" are different problems. We solve seamless
   now and keep every seam that a future meshing layer will need, so scaling up is additive rather
   than a rewrite.
4. **Deterministic simulation.** The world advances in fixed steps independent of wall-clock jitter,
   so behaviour is reproducible and, later, amenable to lockstep/rollback techniques if needed.

## The big picture

```
        ┌────────────────────────────── Server process (1 node) ──────────────────────────────┐
        │                                                                                       │
 UDP    │   IServerTransport ── events ──▶  GameServer ──▶  World (authoritative)              │
 ◀────▶ │   (UdpServerTransport)                │              │                                │
 client │        ▲                              │              ├── entities (source of truth)   │
        │        │        snapshots (per-AoI)   │              └── SpatialGrid (interest mgmt)   │
        │        └──────────────────────────────┘                                               │
        │                              FixedStepLoop drives Tick() at a constant rate            │
        └───────────────────────────────────────────────────────────────────────────────────────┘
```

Each server tick:

1. `GameServer.ProcessNetwork()` drains all transport events — new peers, received packets,
   timeouts — and applies validated input to the world.
2. `World.Step(dt)` advances the simulation one fixed step (movement today; combat, abilities,
   AI, physics later).
3. `GameServer` builds **one snapshot per connected player**, containing only the entities inside
   that player's area of interest, and sends it.

## Key components

### Fixed-timestep loop (`FixedStepLoop`)

The simulation must not depend on how fast the machine happens to be. The loop advances the world in
equal `1/TickRate`-second steps and sleeps between them, with a catch-up cap so a stall can't trigger
a "spiral of death" of replayed steps. Tick rate is `SimulationConstants.TickRate` (20 Hz today).

### Authoritative world (`World`, `ServerEntity`)

`World` is the single source of truth. All mutation happens here, on the simulation thread. Inputs
are clamped (you cannot move faster by sending a bigger vector) and de-duplicated by sequence number
(UDP can reorder and duplicate). Today entities are plain objects in a dictionary; the intended
evolution is a data-oriented / ECS layout once per-tick entity work grows.

### Interest management (`SpatialGrid`)

A uniform spatial hash. Entities are bucketed into fixed-size cells; "who is near point P?" touches
only the cells overlapping P's radius instead of scanning all N entities (O(N²) → roughly O(1) per
query for uniform density). This is the mechanism that keeps **per-client bandwidth bounded** no
matter how large the world or player count becomes: a client is only ever sent its own
neighbourhood.

### Transport abstraction (`ITransport` + `UdpServerTransport`/`UdpClientTransport`)

Game logic never references a concrete networking library. The skeleton ships a raw-UDP transport
(unreliable, unordered — which is fine, because each snapshot is a complete picture of the client's
AoI, so a dropped one is simply superseded by the next). Anything needing reliability (handshake,
chat, trades) will move onto a transport that offers reliable channels — LiteNetLib or ENet — dropped
in behind the same interface without touching the simulation.

### Content & combat (`Data/`, `Combat/`, `World`)

Content — races, classes, abilities, monsters — is **data-driven**: plain definitions held by a
`GameData` registry, with built-in defaults plus optional JSON overrides loaded from the server's
`data/` folder (`System.Text.Json`, in-box). A player's stats are `class base + race modifiers`;
monster stats come from the monster definition.

Combat is resolved by the authoritative `World`: a validated `UseAbility` checks liveness, cooldown,
and range, then applies `max(1, ability.BaseDamage + attackerAttackPower - targetDefense)`. Death
pulls the entity out of the interest grid (invisible and untargetable) and schedules a respawn;
outcomes go out as AoI-gated `CombatEvent` messages. Monster AI runs server-side each tick: acquire
the nearest player in aggro range, chase to ability range, attack on cooldown. See
[ADR-0005](adr/0005-data-driven-content-and-combat.md).

### Wire protocol (`Protocol/`)

Every packet is one UDP datagram: `[1-byte MessageType][payload]`, little-endian, built and parsed by
`PacketWriter`/`PacketReader`. The reader is bounds-checked — **all inbound bytes are treated as
hostile** and malformed packets are dropped, never allowed to throw into the simulation. Message
encode/decode live side by side per message type so the client and server formats can't silently
drift. Bump `SimulationConstants.ProtocolVersion` on any wire change; the handshake rejects mismatches.

## The seamless → massive path (server meshing)

A single node running the loop above is a genuinely seamless world for as many players as one process
can simulate and serve (hundreds, with tuning). To go beyond one node **without** introducing seams:

1. Partition the world's grid cells across multiple server nodes; each node authoritatively simulates
   its cells.
2. Give adjacent nodes an **overlap/handover band** at their shared borders. As a player crosses,
   both nodes simulate them briefly and hand authority over; the player never notices.
3. Interest queries near a border consult the neighbouring node's cells as well.

Because the grid and per-client AoI already exist, meshing becomes "assign cells to nodes and sync
the borders" rather than a re-architecture. That is the whole point of paying for interest management
on day one. Server meshing is genuinely hard (it is where large seamless MMOs spend years); we defer
building it until a single node is actually saturated, but we never design ourselves out of it.

## What is intentionally NOT here yet

Persistence (Postgres/Redis), reliable channels, client-side prediction and interpolation, snapshot
delta-compression, authentication, richer combat (multi-ability bars, resources/mana, cast times,
threat, pathfinding), and anti-cheat beyond server authority. The Unity rendering client is also still
to come. Each has a place in the [ROADMAP](ROADMAP.md); none of them changes the shape above.
