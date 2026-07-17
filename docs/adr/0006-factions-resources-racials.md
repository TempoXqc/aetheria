# ADR-0006: Factions, class/race matrix, resources, and racials

- **Status:** Accepted
- **Context date:** M3 Étape A

## Context

The game needs a recognizable MMO character system: two opposing factions, a set of races and classes
with balance constraints, per-class resource economies, and racial identity — all without hardcoding
content or coupling the simulation to it.

## Decision

**Two factions** (Alliance: Human, Dwarf · Horde: Orc, Elf), stored on each race and propagated to the
player entity and snapshots (so clients can colour/target by faction and PvP rules can key off it).
Gender is carried but **purely cosmetic** — it has no stat or gameplay effect.

**A class/race balance matrix** — each race lists the class ids it may play — is enforced at the
handshake: an illegal combination is rejected with a reason. The matrix is data (JSON), so rebalancing
is an edit, not a code change. Each faction has access to all three classes across its two races.

**Per-class resources.** Warrior=Rage (starts empty, built by dealing/taking damage, decays out of
combat), Mage=Mana (regenerates passively), Ranger=Energy (regenerates quickly). Abilities carry a
resource cost the server validates and spends. This is authoritative like all combat.

**Racial abilities.** Each race has one unique racial (Human Second Wind heal, Dwarf Stoneform
defense, Orc Blood Fury attack, Elf Nature's Swiftness speed), self-cast, no resource cost, long
cooldown, built on a small **timed-effect system**: buffs contribute to *effective* stats
(attack/defense/move speed) until they expire; instant effects (heal, resource restore) apply at once.

## Consequences

- **Positive:** balance and rosters are data; adding a race/class/racial does not touch the protocol
  or simulation.
- **Positive:** effective-stat buffs and resource costs are computed server-side, so they are
  cheat-safe and reused uniformly by players and (future) monster abilities.
- **Positive:** faction on the entity/snapshot is the hook future PvP rules and world-boss contests
  will key off.
- **Negative:** resource/racial tuning values are first-pass and will need iteration with real content.
  They are isolated in data and in `ServerEntity`/`World` for that reason.
- **Negative:** one racial and one basic ability per class is intentionally minimal; multi-ability
  bars, cast times, and resources-as-combos come later (M3+).
