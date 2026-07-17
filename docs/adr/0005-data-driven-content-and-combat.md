# ADR-0005: Data-driven content and authoritative combat

- **Status:** Accepted
- **Context date:** M2 (server gameplay)

## Context

The game needs classes, races, abilities, and monsters, and it needs combat that is safe for PvP. Two
questions: where does content live, and who resolves a fight?

## Decision

**Content is data, not code.** Races, classes, abilities, and monsters are defined as plain DTOs and
loaded by a `GameData` registry. The registry ships built-in defaults *and* loads optional JSON
overrides from `src/Aetheria.Server/data/` using `System.Text.Json` (in-box, so still zero external
dependencies — consistent with ADR-0004). A designer can retune damage, health, or aggro radius by
editing JSON without recompiling. Unknown ids resolve to a sensible default rather than throwing.

**Combat is fully server-authoritative** (per ADR-0001). A client sends `UseAbility(abilityId,
targetId)`; the server validates that attacker and target are alive, the ability is off cooldown, and
the target is within the ability's range, then computes damage as
`max(1, ability.BaseDamage + attacker.AttackPower - target.Defense)` and applies it. Death removes the
entity from the interest grid (so it is neither visible nor targetable) and schedules a respawn.
Combat results are broadcast as `CombatEvent` messages, gated by area of interest like snapshots.

Monster AI runs on the server each tick: acquire the nearest player within aggro radius, chase until
in ability range, then attack on cooldown.

## Consequences

- **Positive:** balance changes are data edits; the wire protocol and simulation are unchanged by
  adding a class or monster.
- **Positive:** no combat outcome is ever decided by a client — the requirement for hardcore PvP.
- **Positive:** health and combat events are AoI-gated, so combat inherits the same scalability
  property as movement (ADR-0002).
- **Negative:** the AI is intentionally minimal (nearest-target chase-and-hit); no pathfinding, threat
  tables, or ability selection yet. Those are future work, not part of this decision.
- **Negative:** damage/stat formulas are deliberately simple and will need iteration once real classes
  and itemization exist; they are isolated in `World.DealDamage` / `StatBlock` for that reason.
