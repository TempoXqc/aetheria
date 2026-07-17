# Contributing

Conventions for working in this repository. They're lightweight on purpose — enough structure to keep
history readable and CI green, no ceremony.

## Branching

- `main` is always green (CI passes) and always runnable.
- Work on short-lived branches off `main`:
  - `feat/<slug>` — a new capability (e.g. `feat/combat-resolution`)
  - `fix/<slug>` — a bug fix
  - `refactor/<slug>`, `docs/<slug>`, `chore/<slug>` — as named
- Open a pull request into `main`. CI (build with warnings-as-errors + tests) must pass to merge.

## Commits

Use [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<optional scope>): <summary>

<optional body — the "why", not the "what">
```

Types: `feat`, `fix`, `refactor`, `perf`, `docs`, `test`, `build`, `ci`, `chore`.

Examples:

```
feat(world): add authoritative ability resolution
fix(net): drop duplicate input packets by sequence
perf(spatial): reuse the query scratch buffer per tick
```

## Code style

- Enforced by `.editorconfig` and the .NET analyzers; the build treats warnings as errors.
- Run `dotnet format Aetheria.slnx` before pushing if your editor doesn't format on save.
- Keep `.csproj` files thin — shared build policy lives in `Directory.Build.props`.

## Architecture decisions

Non-trivial technical choices get a short ADR in `docs/adr/` (copy the format of the existing ones).
Record the decision when you make it, not months later.

## Tests

- New server/simulation logic ships with tests in `tests/Aetheria.Tests`.
- Today that's the zero-dependency `MiniTest` runner; assertions mirror xUnit so the M1 migration is
  mechanical. A failing test fails CI.
