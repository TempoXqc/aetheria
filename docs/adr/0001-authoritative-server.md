# ADR-0001: Server-authoritative simulation

- **Status:** Accepted
- **Context date:** M0

## Context

Aetheria targets hardcore PvP and PvE with real stakes. In any competitive online game the client
runs on a machine the player fully controls and can therefore modify. If the client is trusted for
position, hit detection, damage, or loot, cheating is trivial and the economy and PvP are worthless.

## Decision

The **server is authoritative** for all game state. Clients send *intent* (movement, ability use);
the server validates it, simulates the outcome, and streams results back. Concretely:

- All world mutation happens in `World`, on the simulation thread.
- Inbound input is clamped (e.g. movement vectors normalized to unit length so an inflated vector
  cannot increase speed) and de-duplicated by sequence number.
- The client renders and predicts, but never decides.

## Consequences

- **Positive:** cheat resistance by construction; a single source of truth; a clean place to add
  anti-cheat sanity checks later.
- **Positive:** clients can be "dumb" and interchangeable (headless test client today, Unity later).
- **Negative:** the server does more work per player (mitigated by interest management, ADR-0002).
- **Negative:** perceived input latency requires client-side prediction/reconciliation later (M1).
