# Cohesion Copilot Instructions

This file mirrors the root `AGENTS.md` for GitHub Copilot and other GitHub-native assistants.

If this file and `AGENTS.md` ever drift, treat `AGENTS.md` as the canonical source and update this file to match it.

## Canonical Rules

- Always follow the root `AGENTS.md` before applying any local heuristic.
- Keep implementation aligned with the Cohesion backlog requirements, but use `AGENTS.md` for repo-wide coding rules.
- Use `AGENTS.md` as the source of truth for development environment, repository structure, build-system conventions, and coding standards.
- Preserve the L1, L2, and L3 layering model and the service-root dependency style already established in the repo.
- Placeholder folders and placeholder projects are not final architecture boundaries. Add projects when needed to preserve modularity and clean dependency flow.
- All work must remain NativeAOT-compatible.

## GitHub Project Execution Metadata

- Treat Cohesion GitHub Project fields as execution guidance, not as decorative labels.
- `Priority` expresses urgency and criticality. Lower numbers such as `P001` come before `P002`.
- `Wave` expresses planned delivery order. Lower numbers such as `W01` come before `W02` and `W03`.
- When choosing work autonomously, prefer unblocked items in the earliest available `Priority` and `Wave`.
- Do not pull later-wave work forward ahead of earlier-wave blockers unless the user explicitly asks for it or the dependency graph requires prerequisite work.
- If issue details, dependency relationships, `Priority`, and `Wave` conflict, follow this order: explicit user instruction, dependency or blocker relationships, `Priority`, then `Wave`.
- Preserve later-wave requirements in planning notes even when implementing only current-wave scope.

## Backlog Authoring Guidance

- When creating or refining backlog items, include architectural boundary guidance when it helps a future implementation session make better decisions.
- If a design naturally decomposes into a project family, call out the suggested project family in the feature or story body.
- Suggested project families are advisory unless the issue explicitly marks them as required.
- When suggesting a project family, include candidate project names, project ownership boundaries, and the intended dependency direction between them.
- Call out boundaries that matter for AOT, source generation, validation, serialization, transport, or service integration so later implementation does not need to rediscover them.

## Required Patterns

- Use file-scoped namespaces only.
- Use `CohesionProjectReference` for internal project dependencies.
- Use `CohesionPackageReference` for NuGet dependencies.
- Keep namespace and assembly name aligned exactly.
- Keep public APIs documented with XML comments.
- Use the .NET 10 `extension(...)` syntax for new extension members.
- Prefer direct `throw` statements or reusable extension type methods over throw-helper classes.
- Use explicit `using` directives in each file; do not rely on global usings.
- Prefer interface-first public APIs and keep implementation types `internal` unless a public type is required.
- Keep custom exception roots scoped to the owning library or service family, for example `FileSystemException` or `HttpException`.
- Prefer area-root exceptions that inherit directly from `Exception` or `SystemException` instead of introducing cross-framework exception ancestry.
- When multiple files implement variants of the same abstraction root, prefer grouped root-first filenames such as `Http2Frame.Header.cs` and `Http2Frame.Ping.cs` so related files sort together.
- Project-level documentation should live in a `docs/` folder next to `src/` and `tests/`, with required `OVERVIEW.md`, `DESIGN.md`, and `Assembly/` API reference content.
- Area roots such as `resources/Web/` or `libraries/Core/` should have a `README.md` overview file.
- API reference files under `docs/Assembly/` may mirror namespace and type names directly, for example `docs/Assembly/System.IO/Glob.md`.

## Forbidden Patterns

- Do not use block-scoped namespaces.
- Do not use direct `ProjectReference` paths for internal dependencies.
- Do not use direct `PackageReference` items.
- Do not add new `Microsoft.Extensions.*` package dependencies.
- Do not create public types without XML documentation.
- Do not introduce `ThrowHelper` or `ThrowHelpers` types.
- Do not declare new extension members with the legacy `this` parameter syntax.
- Do not create or revive cross-framework base exception types such as `CohesionException` or `NetworkException`.
- Do not rely on reflection-heavy runtime discovery, dynamic code generation, or other linker-hostile patterns.

## Project and Build Rules

- Libraries target `net10.0` through the repo build system.
- Preview language features must remain enabled.
- Markdown files should use uppercase naming conventions such as `README.md` and `CONTRIBUTING.md`.
- New package versions belong in `build/Targets/Build.References.Packages.targets` before they are consumed.
- Respect the repo folder conventions for `src`, `tests`, `Abstractions`, `Extensions`, `Internal`, `Exceptions`, and `ValueObjects`.
- Respect the repo documentation conventions for sibling `docs/` folders, project-level `OVERVIEW.md` and `DESIGN.md`, and namespace or type-based API reference material in `docs/Assembly/`.

## Testing and AOT Rules

- Add or update tests for behavior changes.
- Spec-driven services need unit tests plus compliance or interoperability tests.
- Async APIs should accept `CancellationToken` and use the `Async` suffix.
- Avoid `async void` except for event handlers.
- Preserve NativeAOT and trimming compatibility in code and tests.

## Practical Guidance for Copilot

- Before suggesting a new abstraction, check whether one already exists in the same service root or shared library.
- When editing project files, prefer Cohesion-specific build items over stock MSBuild items.
- When touching service composition, remember that nested host composition is intentional in this repo and should use the hosting abstractions rather than ad hoc orchestration.
- When implementing service features, keep service-specific requirements in the GitHub backlog issue body in addition to the repo-wide rules in `AGENTS.md`.
