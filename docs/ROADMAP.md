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

### Étape C — Grouping ✅

Party system: invite (same faction only) / accept / decline / leave, leader promotion on leave,
disband at one member, 40-player cap, roster broadcast to members. The party is the entry unit for
instances and raids.

### Étape D — Instances, raids & world bosses ✅ (core)

A `WorldManager` owns the shared open world plus per-group **instance** worlds (all sharing one
entity-id allocator so players transfer seamlessly, keeping their identity). Instances are
**data-driven templates** (spawn lists) whose monsters **scale with group size** (health/damage
multipliers). **Raids** are instances requiring **6–40 players** (leader-triggered, party required).
**Dungeons are NOT instanced** — an elite camp with the Goblin King lives in the open world where
rival factions can fight over it — and **Ashmaw the Devourer**, a raid-difficulty **world boss**,
roams the open world un-instanced (PvP possible). The **faction rule** landed with it: players cannot
attack their own camp; opposite factions can.

- [ ] Instance completion/reset logic, loot lockouts, per-boss mechanics.
- [ ] Entrance objects in the world (currently entered via a request from anywhere).
- [ ] World-boss respawn timers and announcements.

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

### Étape F — Hardcore death & the account bank ✅ (core)

**Permadeath**: on death a character resets to a fresh level 1 — XP, level, and trained skills are
wiped, carried inventory/gear/gold drop to the full-loot corpse, and a new starter kit is granted. A
**persistent account bank** (keyed by account id, in-memory for the server's lifetime) holds gold and
items **across deaths** — deposit before you die and it survives. Verified: a character banked 40 gold,
levelled up, died (level → 1, carried gold dropped), and the 40 banked gold was untouched.

- [ ] Bank access gated to a banker NPC / safe zone (currently allowed anywhere).
- [ ] Durable, account-authenticated bank + name reservation via persistence (M4).
- [ ] Optional: keep a character slot / "new character" flow instead of in-place reroll.

## M4 — Persistence & identity ✅ (core)

Durable state behind an `IPersistenceStore` seam: a **JSON file store with atomic writes** today,
Postgres as a drop-in implementation later. **Account auth** (secret set on first connect, SHA-256
verified after; wrong secret rejected), **durable server-wide name ownership** (a name belongs to its
account even offline and across restarts), **character persistence** (XP/level, gold, inventory,
equipment, skill lines — captured on disconnect, periodically, and immediately on player death) and
**durable banks**. Verified end-to-end: server killed and restarted — character restored (level/XP/
gold), bank intact, wrong secret rejected, name theft rejected.

- [ ] Swap file store for **Postgres**; add **Redis** for hot/shared state (multi-process).
- [ ] Session tokens + handshake on a reliable channel (with the M1 transport swap).
- [ ] Character-slot management (multiple characters per account UI/flow, delete/free names).

## The Unity client ✅ (first playable)

`unity/AetheriaClient`: isometric client consuming the **netstandard2.1 build of Aetheria.Shared**
(same protocol code as the server — zero drift), zero-setup bootstrap (press Play in an empty scene),
interpolated entity views, faction/boss colouring, health bars, full controls (move/target/abilities/
racial/loot/party/instances/bank), login screen enforcing the race/class matrix. See `unity/README.md`.

- [ ] Client-side prediction/reconciliation and fixed-delay interpolation (M1).
- [ ] Real models/animations, real UI (replace OnGUI), nameplates (needs a name-sync message).

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
