# ADR-0011: Parties, scaled instances, open-world dungeons & world bosses

- **Status:** Accepted
- **Context date:** M3 Étapes C & D

## Context

Group play needed three shapes with different rules: private scaled content (instances/raids), shared
contested content (open-world dungeons), and shared raid-difficulty content (world bosses) — plus the
party system underneath and the two-camp PvP rule that makes "contested" mean something.

## Decision

**Parties** (`PartyManager`, pure and unit-tested): leader-based invite/accept/decline/leave, same
faction only, leader promotion on leave, disband when one member would remain, 40-player cap. The
party is the unit that enters instances.

**Multiple worlds under one server** (`WorldManager`): the single seamless open world plus any number
of private instance worlds created per group from a data-driven `InstanceDefinition` (spawn list +
scaling factors + min/max players). All worlds share one **entity-id allocator**, so a player keeps
their entity id as they transfer between worlds (`RemoveForTransfer`/`AdoptEntity`) — no re-handshake,
no client-visible identity change. Empty instances are destroyed. Each world runs its own tick, AoI
snapshots, and combat events; sessions route to the world they inhabit, so nothing leaks between
worlds. This one-server/many-worlds shape is also the seam toward server meshing (M6).

**Scaling**: instance monsters spawn with `mult = 1 + perExtraPlayer × (groupSize − 1)` on health and
damage. **Raids** are instances with `IsRaid`, `MinPlayers = 6`, `MaxPlayers = 40`, leader-triggered.

**Open-world dungeons are NOT instanced** — they are elite camps in the shared world (Goblin King) —
and **world raid bosses** (Ashmaw the Devourer) also live un-instanced in the open world. Because of
the **faction rule** (players cannot attack their own camp; opposite factions can), both are contested
PvP hotspots by construction.

## Consequences

- **Positive:** the three content shapes the design called for exist with the right rules each;
  private content scales instead of gating on fixed group sizes.
- **Positive:** world isolation came free from the existing architecture (each `World` already owned
  its grid/events); the id allocator was the only new invariant needed for seamless transfer.
- **Negative / deferred:** instances have no completion/reset or loot lockout; entry is by request
  from anywhere (no entrance objects in the world yet); world bosses don't respawn on a timer or
  announce; parties don't yet share XP or loot rules. All noted on the roadmap.
