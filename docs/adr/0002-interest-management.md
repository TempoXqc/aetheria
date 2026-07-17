# ADR-0002: Interest management via a uniform spatial grid

- **Status:** Accepted
- **Context date:** M0

## Context

A seamless world means many entities in one continuous space. Naively, telling every client about
every entity is O(N²) in both CPU and bandwidth and collapses well before "massive". We need each
client to receive only what is relevant to it — and we need that mechanism in place *before* building
content, because retrofitting it into a shipping protocol is painful.

## Decision

Bucket entities into a **uniform spatial hash grid** (`SpatialGrid`) with a fixed cell size. Answer
"which entities are within radius R of point P?" by scanning only the cells overlapping that circle,
then refining by exact distance. Each tick, every player receives a snapshot containing only the
entities inside their **area of interest** (radius `AreaOfInterestRadius`).

Cell size and AoI radius are tunable constants; the grid is rebucketed as entities move.

## Alternatives considered

- **Quadtree / loose quadtree:** better for highly non-uniform density, but more complex and with
  worse constant factors for the mostly-uniform densities we expect. Can replace `SpatialGrid` behind
  the same "insert / update / query radius" surface if profiling justifies it.
- **Send-everything:** rejected — does not scale past a trivial player count.

## Consequences

- **Positive:** per-client bandwidth is bounded by local density, not world size.
- **Positive:** the grid is the natural unit of work for **server meshing** (ADR-0003 / ARCHITECTURE):
  assigning cell ranges to nodes is how we scale out later without seams.
- **Negative:** objects that must be globally visible (e.g. a world boss health bar, region chat)
  need a separate broadcast channel — acceptable and explicit.
