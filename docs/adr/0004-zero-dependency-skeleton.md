# ADR-0004: A zero-dependency walking skeleton

- **Status:** Accepted
- **Context date:** M0

## Context

The initial goal is to prove the *shape* of the system end to end — authoritative loop, transport,
protocol, interest management — not to pick every final library. Pulling in networking, logging, DI,
and test frameworks on day one adds version-management overhead and can obscure how the core actually
works.

## Decision

The M0 skeleton uses **only the .NET base class library**. No external NuGet packages. Two pieces are
therefore hand-rolled and deliberately minimal:

- The **UDP transport**, on `System.Net.Sockets`, behind `ITransport` (ADR-0003).
- A tiny reflection-based **test runner** (`MiniTest`) whose assertions mirror xUnit's shape.

Central Package Management is already configured (`Directory.Packages.props`) with the planned
first dependencies listed as comments, so adding them later is a one-line change per package.

## Consequences

- **Positive:** the solution builds, runs, and tests with just the SDK — no restore, no version drift,
  trivial to reproduce and to reason about.
- **Positive:** every hand-rolled piece sits behind a seam, so replacing it with a real library
  (LiteNetLib, xUnit, Serilog) is mechanical and localized.
- **Negative:** the hand-rolled pieces are intentionally not production-grade. They are scaffolding;
  M1 replaces them. This ADR exists so that is a conscious, recorded choice rather than an oversight.
