# Roadmap

Milestones are ordered so that every step keeps the project shippable and each new system slots into
the architecture without a rewrite. Scope is a small team / solo dev; nothing here assumes a studio.

## M0 — Walking skeleton ✅ (this commit)

- [x] Solution scaffold, coding standards, central package management, CI.
- [x] Fixed-timestep authoritative server loop.
- [x] Raw-UDP transport behind an `ITransport` abstraction.
- [x] Binary wire protocol with a hardened, bounds-checked reader.
- [x] Spatial-grid interest management.
- [x] Movement: client input → authoritative server movement → per-client AoI snapshots.
- [x] Headless test client; two clients see each other and cull past the AoI radius.
- [x] Unit tests for grid, protocol, and world.

## M1 — Hardening the foundation

- [ ] Swap the hand-rolled UDP transport for **LiteNetLib** (or ENet) behind `ITransport`: reliable +
      unreliable channels, real connection management, MTU-aware fragmentation.
- [ ] Migrate tests to **xUnit** (`[Test]` → `[Fact]`; assertions already mirror xUnit).
- [ ] Structured logging (Serilog) and a `Microsoft.Extensions.Hosting` host with config/DI.
- [ ] Snapshot **delta compression** and per-entity relevancy (only send what changed).
- [ ] Client-side **prediction + reconciliation** and entity **interpolation** (needs the real client).

## M2 — The game starts to exist

- [x] Classes and races as data-driven definitions (JSON via in-box System.Text.Json).
- [x] Core combat: authoritative ability/damage resolution, cooldowns, death/respawn.
- [x] Basic PvE: monster entities, simple aggro/chase/attack AI on the server, monster respawn.
- [x] Health in snapshots + combat events broadcast to nearby players (AoI-gated).
- [ ] Unity client project: isometric 3D rendering, camera, input → `InputCommand`, snapshot interp.
- [ ] Targeting UX, ability bar, floating damage numbers (needs the client).
- [ ] More abilities per class (multi-ability bars, resources/mana, cast times).

## M3 — Persistence & identity

- [ ] **Postgres** for durable state (accounts, characters, inventory) via a repository layer.
- [ ] **Redis** for hot/shared state and cross-process coordination.
- [ ] Authentication and session tokens; move the handshake onto a reliable channel.
- [ ] Character creation and selection flow.

## M4 — PvP & systems depth

- [ ] Structured PvP (zones/rules, factions), hardcore death stakes.
- [ ] Inventory, items, loot tables, trading (reliable, transactional).
- [ ] Anti-cheat hardening beyond server authority (rate limits, sanity checks, telemetry).

## M5 — Scale-out (server meshing)

- [ ] Split the grid across multiple authoritative nodes.
- [ ] Border overlap bands and seamless authority handover between nodes.
- [ ] Cross-node interest queries; load-based cell reassignment.

## M6 — Ship

- [ ] **Steam** integration via Facepunch.Steamworks (auth, relay to hide IPs in PvP, presence).
- [ ] Dedicated-server deployment, build pipeline, telemetry/observability.
- [ ] Playtest, balance, and release hardening.

---

**Guiding rule:** do not start a milestone by scaling. Add content and systems on the single seamless
node until it is genuinely saturated; only then build meshing (M5). The interest-management seam is
already in place so that step is additive.
