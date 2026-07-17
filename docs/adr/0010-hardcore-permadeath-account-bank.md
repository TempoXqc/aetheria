# ADR-0010: Hardcore permadeath and the account bank

- **Status:** Accepted
- **Context date:** M3 Étape F

## Context

Death needed real stakes (hardcore) without being so punishing that players won't re-roll. The agreed
design (ADR-0007) is permadeath softened by a persistent account bank. This implements it ahead of the
persistence layer (M4), so the model is in place and durable storage is a later swap.

## Decision

**Permadeath resets the character.** When a player dies, their carried inventory, equipped gear, and
gold drop to a full-loot corpse (Étape B), then the character's **progression is wiped** — XP → 0,
level → 1, trained skill lines cleared, progression bonuses zeroed — and it is re-granted a fresh
starter kit. The normal respawn then returns the reborn, from-scratch character. (This is an in-place
reroll rather than a separate character-slot flow; that refinement is on the roadmap.)

**The account bank is separate and survives death.** A bank is a gold + item store keyed by an
**account id** the client supplies at handshake, held by the `GameServer` in a per-account map (not by
the world, not by the character). It is **not touched** by death. Players deposit/withdraw gold and
items via a `BankTransaction` message; `BankService` performs the transfers as pure functions over two
inventories (fully unit-tested). So: what you carry drops on death, what you banked is safe — the meta
progression that lets you "start over, but not from zero".

The bank map and the unique-name set are **in-memory for the server's lifetime**. Real durability
(surviving restarts, account authentication) arrives with persistence (M4).

## Consequences

- **Positive:** hardcore stakes are real (you lose your character's power and carried goods), but the
  bank gives a reason to keep playing and a way to bootstrap the next life.
- **Positive:** bank logic is transport-free and pure, so it is trivially testable and will port
  directly onto a database-backed store in M4 (Account → Bank, already modelled separately from
  Account → Character, per ADR-0007).
- **Negative / deferred:** the account id is currently client-supplied and unauthenticated (anyone
  could claim any account's bank) — acceptable for the pre-auth skeleton, closed by M4 auth. Bank
  access isn't yet gated to a banker/safe zone, and the re-kit grants a little free starting gold each
  death (a minor economy leak to tune).
