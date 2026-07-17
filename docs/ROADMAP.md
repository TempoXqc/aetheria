# Roadmap

Milestones are ordered so that every step keeps the project shippable and each new system slots into
the architecture without a rewrite. Scope is a small team / solo dev; nothing here assumes a studio.

## M0 — Walking skeleton ✅

Authoritative fixed-timestep loop, raw-UDP transport behind `ITransport`, hardened binary protocol,
spatial-grid interest management, movement (input → authoritative → per-client AoI snapshots),
headless test client, unit tests.

## M1 — Hardening the foundation

- [ ] Swap the hand-rolled UDP transport for **LiteNetLib** (or ENet) behind `ITransport`: reliable +
      unreliable channels, connection management, MTU-aware fragmentation.
- [ ] Migrate tests to **xUnit** (`[Test]` → `[Fact]`; assertions already mirror xUnit).
- [ ] Structured logging (Serilog) and a `Microsoft.Extensions.Hosting` host with config/DI.
- [ ] Snapshot **delta compression** and per-entity relevancy.
- [ ] Client-side **prediction + reconciliation** and entity **interpolation** (needs the real client).

## M2 — Combat & PvE ✅

Data-driven classes/races/abilities/monsters (JSON), authoritative combat (range/cooldown/damage),
death & respawn, PvE monster AI (aggro/chase/attack), health in snapshots, AoI-gated combat events.

## M3 — Characters, world & economy (in progress)

### Étape A — Character system ✅

Two factions (Alliance: Human, Dwarf · Horde: Orc, Elf), gender (cosmetic), a class/race balance
matrix enforced at handshake, per-class resources (Warrior=Rage, Mage=Mana, Ranger=Energy) with
regen/decay and ability costs, and a unique racial ability per race (heal / attack / defense / speed)
built on a timed-effect system.

### Étape B — Inventory, items & full-loot corpses

- [ ] Item definitions (data-driven) + player inventory and equipment slots.
- [ ] On death, the body becomes a **lootable corpse container** left in the world holding the
      player's inventory + equipment. **Full loot: any player can loot it.** The corpse remains until
      emptied, then despawns.
- [ ] Loot interaction messages (open corpse, take item), AoI-gated like everything else.

### Étape C — Grouping

- [ ] Party system: invite / accept / leave, group roster, shared context for instances.

### Étape D — Instances, raids & world bosses

- [ ] **Instanced** content: solo/party **instances that scale** with group size; **raids**
      instanceable and scaled, requiring **6–40 players**.
- [ ] **Dungeons are NOT instanced** — they live in the open world, so **PvP is possible** there.
- [ ] **World raid bosses**: raid-difficulty bosses placed in the open (non-instanced) world; PvP
      possible around them.
- [ ] Architecture: a `WorldManager` owning multiple `World` instances (the shared open world plus
      instanced copies); players are assigned to one; the interest grid already localizes work per
      instance. This is also the seam toward server meshing (M6).

### Étape E — Progression (XP-driven, not expansion-style levels)

- [ ] **A small, fixed set of levels** whose only role is to **unlock spells and gate access to
      events/content** — deliberately NOT a WoW-style ever-inflating level treadmill.
- [ ] Primary power growth is **stat evolution from experience**: killing monsters and completing
      events grants XP that raises the character's stats over time.
- [ ] Two intertwined tracks: discrete unlocks (levels → abilities/events) and continuous stat growth
      (XP → stats). Both are data-driven (curves/thresholds in JSON).

### Étape F — Hardcore death & the account bank

- [ ] **Permadeath**: when a character dies for good, it resets — you start a new character from
      scratch.
- [ ] A **persistent account bank** stores gold and materials/items across deaths, so a fresh start
      begins with your stash rather than truly from zero.
- [ ] Interaction with full-loot corpses (Étape B): gear you were carrying drops and is lootable;
      what you deposited in the bank beforehand is safe.

## M4 — Persistence & identity

- [ ] **Postgres** for durable state (accounts, the bank, characters, unlocks, progression) via a
      repository layer.
- [ ] **Redis** for hot/shared state and cross-process coordination.
- [ ] Authentication and session tokens; move the handshake onto a reliable channel.

## M5 — Steam & ship

- [ ] **Steam** integration via Facepunch.Steamworks (auth, relay to hide IPs in PvP, presence).
- [ ] Dedicated-server deployment, build pipeline, telemetry/observability.
- [ ] Playtest, balance, release hardening.

## M6 — Scale-out (server meshing)

- [ ] Split the grid across multiple authoritative nodes; border overlap bands and seamless authority
      handover; cross-node interest queries; load-based cell reassignment.

---

**Guiding rule:** do not start a milestone by scaling. Add content and systems on the single seamless
node until it is genuinely saturated; only then build meshing (M6). The interest-management seam is
already in place so that step is additive.
