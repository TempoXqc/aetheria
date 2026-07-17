# ADR-0003: Transport behind an interface; raw UDP for the skeleton

- **Status:** Accepted
- **Context date:** M0

## Context

Real-time games need UDP-style messaging (low latency, tolerate loss) rather than TCP head-of-line
blocking. Production-grade netcode also needs reliable channels, fragmentation, encryption, and
connection management — which is exactly what libraries like LiteNetLib and ENet provide. But we do
not want the game simulation coupled to any specific library, and we want the skeleton to build and
run with no external dependencies (ADR-0004).

## Decision

Define `IServerTransport` / `IClientTransport` in `Aetheria.Shared.Net`. Game logic depends only on
these. Ship a raw-UDP implementation (`UdpServerTransport` / `UdpClientTransport`) built on
`System.Net.Sockets` for the skeleton: unreliable, unordered delivery with peer synthesis from remote
endpoints and timeout-based disconnect detection.

Unreliable delivery is acceptable now because each snapshot is a **complete** picture of the client's
AoI — a lost snapshot is simply superseded by the next one.

## Consequences

- **Positive:** the simulation is transport-agnostic; swapping in LiteNetLib/ENet at M1 is a
  contained change behind the interface, with no game-code churn.
- **Positive:** zero-dependency build and fully local, deterministic testing.
- **Negative:** the raw-UDP transport has no reliable channel, so handshake/chat/trades cannot ride
  on it as-is — those wait for the M1 transport swap. It is a scaffold, not the shipping transport.
- **Negative:** no encryption yet; must be addressed before any public exposure.
