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

### Étape B — Inventory, items & full-loot corpses ✅

Data-driven items (weapons/armor with stat bonuses, stackable materials/consumables), player
inventory + equipment slots (weapon/armor) feeding effective stats, and gold. On death the body
becomes a **lootable corpse** holding the player's inventory + equipped gear + gold; **full loot —
any player can loot it** (range-gated); the corpse despawns once emptied. Monster kills grant XP and
gold to the killer.

- [ ] Item-by-item loot windows (currently loot-all on interact) and inventory management (move/drop).
- [ ] Consumable use (e.g. healing potions) and vendors.

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

### Étape E — Progression (XP-driven, not expansion-style levels) ✅ (core)

A small, fixed level cap (data-driven thresholds) whose role is to **unlock abilities** — advanced
class abilities gate on level, deliberately NOT a WoW-style ever-inflating treadmill. Primary power
growth is **continuous stat evolution from XP**: killing monsters grants XP that raises attack,
defense, and **max health / max mana** over time (Rage and Energy stay fixed at 100 — combo-style
resources that don't grow). Plus **weapon/spell proficiency** (early-WoW style): each ability trains a
**skill line** (Swords, Fire, Marksmanship) as it's used, and higher skill makes that style hit harder.
All curves are data-driven.

- [ ] Gate **event/content access** by level (needs an events system).
- [ ] XP from **completing events**, not just kills; per-class ability unlock trees.
- [ ] Surface skill lines to the client (currently server-side only) and cap skill by level.

### Étape A addendum — unique character names ✅

Character names are **unique server-wide across both factions**, validated at the handshake
(length/format + case-insensitive uniqueness), reserved on join and freed on disconnect. Durable
uniqueness (surviving disconnect/relog) arrives with persistence (M4).

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
