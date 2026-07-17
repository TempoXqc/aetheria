# ADR-0009: Weapon/spell proficiency, resource growth, and unique names

- **Status:** Accepted
- **Context date:** M3 (continuation of Étapes A & E)

## Context

Three refinements to the character model, matching the intended early-WoW feel.

## Decision

**Resources grow differently by type.** Max health *and max mana* grow continuously with XP
(`HealthPerXp`, `ManaPerXp`); **Rage and Energy keep a fixed 100 pool** — they are combo-style
resources, not a growing reservoir. Implemented as `EffectiveMaxResource = base + (isMana ?
progressionBonus : 0)`, so only mana users benefit while the wire/regeneration paths stay uniform.

**Weapon/spell proficiency (skill lines).** Each ability declares a `SkillLineId` (Swords, Fire,
Marksmanship; 0 for racials/monster attacks). Using a damaging ability trains that line
(`SkillGainPerUse`, capped at `MaxSkill`), and the line's skill scales the ability's damage
(`DamagePerSkillPoint` → up to +40% at skill 100). So the weapons and spells a character actually uses
get progressively stronger, independent of level. All server-authoritative and data-tuned.

**Unique character names.** Names are unique **server-wide across both factions**, validated at the
handshake (2–16 chars, letters/digits, case-insensitive) and reserved in an in-memory set held by the
`GameServer`, freed on disconnect. A duplicate is rejected with a clear reason.

## Consequences

- **Positive:** growth feels WoW-like — mana pools deepen, favourite weapons/spells sharpen — without
  a level treadmill, and resource semantics differ correctly per class.
- **Positive:** proficiency and resource growth compose with the existing effective-stat pipeline;
  no protocol change was needed for combat (skill acts inside `DealDamage`).
- **Negative / deferred:** skill lines aren't yet sent to the client (no UI), skill isn't capped by
  level, and name uniqueness is only among *active* characters until persistence (M4) makes it durable
  — a relog could currently reclaim or lose a name. Both are noted in the roadmap.
