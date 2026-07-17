# ADR-0008: Items, full-loot corpses, and XP progression

- **Status:** Accepted
- **Context date:** M3 Étapes B & E

## Context

The game needs gear that changes power, a hardcore loot consequence for death, and character growth —
all server-authoritative and consistent with the earlier decisions (data-driven content, cheat-safe
simulation).

## Decision

**Items are data.** `ItemDefinition` (weapons/armor with attack/defense/health bonuses, stackable
materials/consumables, gold value) lives in the registry with JSON overrides. Players carry an
`Inventory` (gold + item stacks, fixed capacity) and equip a weapon + armor slot; equipped bonuses
feed **effective stats** alongside race/class, progression, and buffs.

**Death drops a full-loot corpse.** When a player dies, their carried inventory, equipped gear, and
gold move into a new `Corpse` entity left at the death site. **Any player** may loot it (range-gated,
like all interactions); it despawns once emptied. The dead player respawns empty-handed. Monster kills
instead grant XP + gold directly to the killer. Corpses are lootable, never attackable.

**Progression is XP-driven stat growth with a small unlock ladder** (per ADR-0007). Total XP maps
continuously to flat bonuses on attack/defense/max-health; a small, fixed set of level thresholds
gates **advanced class abilities** (`AbilityDefinition.UnlockLevel`, checked server-side for players).
Curves and thresholds are data.

## Consequences

- **Positive:** gear, drops, and growth are all data + authoritative server logic; no client is
  trusted for loot or power.
- **Positive:** effective-stat computation now composes cleanly: `base(class+race) + equipment +
  progression`, then buff multipliers. One place, reused by combat and snapshots.
- **Positive:** full-loot corpses realize the hardcore stakes without needing permadeath yet; they
  compose directly with the future account bank (ADR-0007 Étape F): carried gear drops, banked goods
  are safe.
- **Negative / deferred:** loot is currently all-at-once on interact (no item-by-item window),
  inventory has no move/drop UI, consumables aren't yet usable, and there are no monster loot tables
  (monsters give XP/gold, not item drops). These are noted in the roadmap.
- **Note:** XP is not lost on the current respawn — true permadeath + reset is Étape F, still pending.
