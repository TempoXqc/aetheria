# ADR-0007: Progression model and hardcore economy

- **Status:** Accepted (design direction) — implementation scheduled for M3 Étapes E/F
- **Context date:** M3

## Context

Two product decisions shape long-term character growth and the stakes of death. They are recorded now
so the data model and systems built beforehand (items, corpses, persistence) account for them, even
though the code lands later.

## Decision

**Progression is XP-driven stat evolution, not an expansion-style level treadmill.** There is a
**small, fixed set of levels** whose *only* purpose is to **unlock spells and gate access to events /
content** — the game does not raise a level cap every expansion. The primary source of character power
is **experience**: killing monsters and completing events grants XP that **raises the character's
stats** continuously. So growth has two intertwined tracks: discrete unlocks (a few levels →
abilities/events) and continuous stat growth (XP → stats). Both are data-driven (thresholds and
growth curves in JSON).

**Death is hardcore, softened by a persistent account bank.** A character that dies for good **resets
to zero** — you roll a new character. However, a **persistent account-level bank** holds gold and
materials/items across deaths, so a fresh start begins with your stash rather than truly from nothing.
This composes with full-loot corpses (ADR/roadmap Étape B): the gear you were *carrying* drops and is
lootable by others; what you *deposited* in the bank beforehand is safe.

## Consequences

- **Positive:** character identity is "how far your stats have evolved", not "which expansion's cap you
  hit" — matches the intended feel and avoids power inflation.
- **Positive:** the bank gives hardcore death real stakes without being punishing enough to stop
  players from re-rolling — the retention lever.
- **Design constraints this imposes on earlier work:**
  - The **bank is account-scoped and must outlive any character** — persistence (M4) models Account →
    Bank separately from Account → Character.
  - Items need a clear split between **carried** (drops to corpse on death) and **banked** (safe).
  - XP and unlock state are **character-scoped** and wiped on permadeath; the bank is not.
- **Negative:** none of this is implemented yet; this ADR is the contract those future stages build to.
  Level count, XP curves, stat-growth formulas, and bank capacity are all still to be tuned in data.
