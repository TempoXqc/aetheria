# ADR-0012: Durable persistence behind a store seam; Unity client on the shared DLL

- **Status:** Accepted
- **Context date:** M4 + first playable client

## Context

Two needs matured at once: durable state (accounts, banks, characters, names must survive restarts —
and permadeath must not be escapable by crashing the server) and a real rendering client. Both had a
constraint: no external packages in the build environment, and no drift between client and server
protocol code.

## Decision

**Persistence sits behind `IPersistenceStore`** (`Load()` / `Save(state)`), with plain serializable
records (`ServerState` → accounts → characters/bank; plus the server-wide name registry). Today's
implementation is a **single JSON file with atomic writes** (serialize to temp, move over — a crash
mid-write can never corrupt the last good state). Postgres later is a new implementation of the same
interface, not a refactor. Save points: **periodic flush** (~5 s), **on disconnect**, and
**immediately on any player death** — so hardcore permadeath itself is never lost to a crash window.

**Accounts authenticate with a secret**: first connect stores its SHA-256; later connects must match.
**Names are durably owned**: the server-wide registry maps name → account and survives restarts, so a
disconnected player's name cannot be taken (the same account can always log its character back in).
**Characters restore**: XP/level (recomputed through the same progression code), gold, inventory,
equipment (with bonuses recomputed), and trained skill lines. Banks hydrate at boot.

**The Unity client consumes the shared layer as a compiled DLL.** `Aetheria.Shared` dual-targets
`net10.0` and `netstandard2.1` (Unity 2022.2+); the few missing runtime APIs are shimmed in one
polyfills file, and JSON content loading is compiled out on netstandard (clients render; they don't
author content). The harness's `GameClient` moved INTO the shared layer, so Unity and the test
harness run the exact same session/protocol code — the wire format cannot drift. The Unity project
itself is zero-setup (a `RuntimeInitializeOnLoadMethod` bootstrap builds camera/ground/client in any
empty scene) and renders interpolated primitive views with an OnGUI HUD.

## Consequences

- **Positive:** real durability with zero external dependencies; permadeath, bank, names, and auth
  all survive `kill -9` (verified end-to-end with a server restart).
- **Positive:** one protocol codebase across server, harness, and Unity.
- **Negative / deferred:** secrets travel in cleartext UDP until the reliable/encrypted transport
  (M1) — acceptable pre-alpha, unacceptable for release. A single JSON file won't scale to thousands
  of accounts (fine now; Postgres swap planned). Unity visuals are placeholder primitives by design.
