# Workflow Conventions

## Commit messages — conventional commits

```
type(scope): subject
```

**Types:** `feat`, `fix`, `docs`, `refactor`, `test`, `chore`.
Examples: `feat(database): add connection pooling support` · `fix(cache): resolve memory leak in expiration logic` · `chore(build): update to .NET 10.0.101`

## Branch naming

- `main` — production-ready · `development` — integration
- `feature/{name}` — new features. Work tracked in the Cohesion GitHub Project uses `feature/<wbs>-<slug>` (e.g., `feature/L01.01.11.14-extended-connect`)
- `fix/{name}` — bug fixes · `docs/{name}` — documentation

## GitHub Project execution metadata

Treat Cohesion GitHub Project fields as execution guidance, not decorative labels:

- `Priority`: lower number = higher priority (`P001` before `P002`). `Wave`: lower number = earlier delivery (`W01` before `W02`).
- When selecting work autonomously, prefer items that are both unblocked and in the earliest available `Priority` and `Wave`. Do not pull later-wave work ahead of earlier-wave blockers unless the user asks or the dependency graph requires it.
- Conflicts resolve in this order: explicit user instruction → dependency/blocker relationships → `Priority` → `Wave`.
- Preserve later-wave requirements in planning notes even when implementing only current-wave scope. If a ticket needs prerequisite work from another ticket, call that out rather than silently reordering.
- Work items follow `[<wbs>] <title>` (area epic `L01.01.NN` → feature `L01.01.NN.MM` → task `L01.01.NN.MM.PP`) in Project #13. Use the `cohesion-work-items` skill to create, place, and link items — especially for capturing scope creep discovered mid-branch.
- When implementing, the GitHub issue body is the authoritative source of service-specific requirements — keep the implementation aligned with it alongside the repo-wide coding rules.

## Backlog authoring

When creating or refining backlog items, include architectural boundary guidance that helps a future implementation session: suggested project families (candidate names, each project's responsibility, dependency direction — advisory unless marked required), and boundaries that matter for AOT, source generation, validation, serialization, transport, or service integration. Use issue bodies to preserve this context even when placeholder folders already exist.
