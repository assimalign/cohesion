---
name: cohesion-coding-rules
description: Enforce Cohesion repo coding standards (AGENTS.md) and prevent guideline drift during .NET development in the assimalign/cohesion repo. Use this skill for any C#, MSBuild, csproj, props, targets, or project-structure work touching files under libraries/, resources/, sdks/, frameworks/, build/, installer/, extensions/, or tooling/. Trigger even when the user does not mention AGENTS.md or coding guidelines — any code change in this repo qualifies, and triggering matters most in long sessions where rules drift. Covers file-scoped namespaces, CohesionProjectReference/CohesionPackageReference, the .NET 10 extension(...) syntax instead of ThrowHelper, centralizing shared MSBuild logic in .props/.targets files instead of duplicating per project, the Framework + SDK architecture, area-scoped exception roots, no Microsoft.Extensions.*, XML docs, naming, AOT compatibility. Also defines the protocol for user-directed exceptions so deviations are explicit, scoped, and documented in code.
---

# Cohesion Coding Rules

The canonical rules live in `AGENTS.md` at the repo root. This skill is a guardrail against drift in long sessions — it does **not** replace `AGENTS.md`. It re-anchors the rules that get partially implemented or forgotten over time, and adds a verification pass before declaring work done.

## At the start of any development task

Do these three things before writing or modifying code:

1. **Re-read `AGENTS.md`** at the repo root if you have not consulted it in this session, or if more than a handful of turns have passed since you last did. The drift this skill exists to prevent comes from operating off stale memory of the rules.
2. **Read the relevant area's `README.md` and any project-level `docs/DESIGN.md`** for the library, resource, framework, or SDK you are touching. Area context often determines which rule variant applies (e.g., area-scoped exception roots, framework membership, layering position).
3. **Briefly state the rules that apply to this task before coding.** A one- or two-line plan that names the relevant patterns (e.g., "interface-first, `extension(...)` registration, add package version to `Build.References.Packages.targets` first") is enough. This forces the rules into the active plan instead of leaving them as background knowledge.

## Drift-prone rules — re-verify each before completion

These are the rules most commonly forgotten or partially implemented in long sessions. Run through this list before declaring a chunk of work done, even if the change feels small.

### Build configuration

- **Shared MSBuild logic lives in `.props` / `.targets` files**, not duplicated in every csproj. Before adding properties or items to a project file, check whether the same setup already exists (or belongs) in `build/Targets/*.props` or `build/Targets/*.targets` and extend the shared file instead. Two or more sibling csprojs carrying the same `<PropertyGroup>` block is a strong signal the block belongs in shared build config. Per-project duplication is the failure mode to watch for.
- **`CohesionProjectReference` for internal deps** — never `ProjectReference` with relative paths.
- **`CohesionPackageReference` for NuGet packages** — never raw `PackageReference`. Add the version to `build/Targets/Build.References.Packages.targets` first, then reference it.
- **Never reference any `Microsoft.Extensions.*` package.** This is a standing architectural commitment, not a stylistic preference.
- **`$(CohesionVersion)` is the single source of truth.** No per-project `<Version>` overrides anywhere.
- **Never hardcode a version on `<Import Sdk>`** — only `<Project Sdk>` honors that syntax. Pin SDK versions in `global.json`'s `msbuild-sdks` block.
- **`FrameworkList.xml` and `RuntimeList.xml` are build artifacts.** Never hand-edit them; the collection target in `App.targets` regenerates them on pack.

### Exception handling

- **No `ThrowHelper` / `ThrowHelpers` types.** Prefer direct `throw` statements. If reusable throw logic is genuinely needed, implement it as a .NET 10 extension type method in `Extensions/`. When touching existing `ThrowHelper` usages, migrate them toward direct throws or extension type methods rather than adding to them.
- **Area-scoped exception roots only.** Use `FileSystemException`, `HttpException`, `DatabaseException`, etc. for libraries/services that need a shared root. Do not introduce framework-wide ancestry like `CohesionException`.
- **Catch specific exceptions, not bare `Exception`.**
- **Always `throw;` to rethrow**, never `throw ex;` (loses stack trace).

### Extension members (the .NET 10 way)

- **Use the `extension(...)` syntax** for all new extension members. The legacy `this T param` form is forbidden in new code.
- Extension containers remain `public static class`; the members go inside `extension(Type instance) { ... }` blocks.
- DI registration extensions, fluent builders, helper methods — all use this form.

### Surface conventions

- **File-scoped namespaces only.** Block-scoped namespaces are forbidden in new files.
- **Namespace matches assembly name exactly.**
- **Interface-first for public APIs.** Public surface is `IFoo`; implementation is `internal class Foo : IFoo`. (See "Handling user-directed exceptions" below if asked to deviate.)
- **Async methods end in `Async`** and accept `CancellationToken cancellationToken = default`.
- **No `async void`** except event handlers.
- **Public APIs require XML documentation** — `<summary>`, `<param>` for each parameter, `<returns>` for non-void, `<exception>` for thrown exceptions.
- **No global usings.** Never add `<Using Include="..." />` items to project files. Use explicit `using` directives per file.

### Naming and file layout

- **One public type per file** (exceptions: nested types, related enums).
- **File name matches primary type name.** For variant families sharing an abstraction root, use grouped root-first naming (e.g., `Http2Frame.Header.cs`, `Http2Frame.Ping.cs`) so related files sort together.
- **Markdown files use UPPERCASE names** (`README.md`, `OVERVIEW.md`, `DESIGN.md`). The single exception is `docs/Assembly/` API reference files which intentionally mirror CLR namespace and type names.
- **Test classes:** `{Feature}Tests`. **Test methods:** `{Method}_{Scenario}_{ExpectedBehavior}`.

### AOT compatibility

- `<IsAotCompatible>true</IsAotCompatible>` is a hard repo-wide requirement.
- No reflection-based serialization, no runtime code generation, no `Assembly.LoadFrom()`. Runtime type inspection must go through source generators.

### Documentation

- **Every library must have `docs/DESIGN.md`** capturing the design decisions behind its shape — design intent, why-this-not-that trade-offs, lifecycle and error model, family relationships, AOT posture, and explicit non-goals. Future sessions read this to understand *why* the code looks the way it does without re-deriving the reasoning from diffs and commit history. The canonical example is `libraries/Dns/Assimalign.Cohesion.Dns/docs/DESIGN.md` — match its depth and structure for new libraries. If you're touching a library that lacks `DESIGN.md`, create one in the same change.
- **Update `DESIGN.md` whenever a code change alters or extends a design decision.** A new lifecycle pattern, a changed error model, a swap from interface to abstract base, a new family member, a relaxed AOT constraint, a shifted non-goal — if it would surprise someone reading only the existing `DESIGN.md`, the doc changes in the same commit as the code. A `DESIGN.md` that lags the code is worse than no `DESIGN.md`, because it actively misleads the next reader. This rule is drift-prone because design changes feel like code changes in the moment; the doc update is the easy thing to skip and the hardest to notice in review.

## Before declaring work done

Read `references/checklist.md` and confirm each applicable item. This applies even to small changes — small changes are the most common source of drift because they feel too minor to verify. If the checklist surfaces something missed, fix it before reporting completion, not after.

## Handling user-directed exceptions to the rules

The user may ask for an approach that contradicts a rule in `AGENTS.md` — for example, asking for abstract classes instead of the interface-first pattern. When you detect such a conflict:

1. **Name the rule explicitly.** Quote or paraphrase the specific rule from `AGENTS.md` that the request deviates from. Do not silently comply — the user may have forgotten the rule exists, and surfacing it is part of the value this skill provides.
2. **Confirm intent before proceeding**, unless the user's message already explicitly acknowledges the deviation (e.g., "I know this breaks the interface-first rule, but..."). A one-line check is enough: *"This deviates from the AGENTS.md interface-first rule. Want to proceed with abstract classes here?"*
3. **Scope the exception narrowly.** The deviation applies only to the specific component, library, or area the user named. The next component in the same session still follows the original rule. Do not generalize.
4. **Document the deviation in code.** Add a short comment at the relevant entry point (e.g., the abstract base class, the deviating project file) so future readers and future Claude sessions understand it is intentional and don't "correct" it back. Format:

   ```csharp
   // Deviates from AGENTS.md <rule name> per design decision: <one-line rationale>.
   ```

   Or for MSBuild files:

   ```xml
   <!-- Deviates from AGENTS.md <rule name> per design decision: <one-line rationale>. -->
   ```

5. **Surface the deviation in the change summary.** When summarizing the work, mention which rule was deviated from and why. This makes the deviation traceable in commit messages and PR descriptions.

### Rules requiring especially explicit confirmation

Some rules are architectural commitments rather than stylistic preferences. Deviating from these has cascading consequences and requires the user to confirm they understand the impact:

- AOT compatibility (`<IsAotCompatible>true</IsAotCompatible>`)
- No `Microsoft.Extensions.*` references
- The `$(CohesionVersion)` single-source-of-truth chain
- The Framework + SDK packaging model (never collapsing a framework's full content under one csproj)
- Never hardcoding a version on `<Import Sdk>`

If asked to deviate from one of these, restate the architectural reason the rule exists (see `AGENTS.md` and `references/build-system.md`) and ask the user to confirm they understand the trade-off before proceeding.

## Reference files

Read these on demand when the task touches their area. They contain the full rule text from `AGENTS.md` reorganized for fast lookup — useful when you need to confirm a specific detail without re-reading the entire `AGENTS.md`.

- `references/general-rules.md` — full required/forbidden patterns, naming conventions, code organization, access modifiers, async/await, exception handling
- `references/build-system.md` — Framework + SDK architecture, MSBuild conventions, props/targets layering, version management, the dev loop
- `references/testing.md` — test naming, AAA pattern, Shouldly assertions
- `references/documentation.md` — XML doc requirements, project-level docs (`OVERVIEW.md`, `DESIGN.md`, `docs/Assembly/`), area README requirements
- `references/checklist.md` — pre-completion verification checklist

**`AGENTS.md` at the repo root remains the canonical source.** When this skill and `AGENTS.md` appear to conflict, `AGENTS.md` wins — and surfacing the conflict back to the user is worthwhile so the skill or the canonical file can be corrected.
