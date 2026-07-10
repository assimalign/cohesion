# Overview

Repository-level documentation for the Cohesion mono repository. Coding standards and agent rules live in [`.claude/rules/`](../.claude/rules/) (auto-loaded by Claude Code; entry point [`.claude/CLAUDE.md`](../.claude/CLAUDE.md)).

**Go To**

- [Delivery Roadmap](./DELIVERY_ROADMAP.md) — delivery waves, initiative sequencing, and the L1/L2/L3 layering model
- [Service Layer Design](./SERVICE_LAYER_DESIGN.md) — high-level design for each service under `resources/`
- [Service Story Requirements](./SERVICE_STORY_REQUIREMENTS.md) — implementation requirements for service-level backlog stories
- Build
  - [Cohesion Custom MSBuild Items](./build/MSBUILD_COHESION_PROPS.md) — `CohesionProjectReference`, `CohesionPackageReference`, code generation
  - [Common MSBuild Properties](./build/MSBUILD_COMMON_PROPS.md) — where shared build properties are defined
  - [Common MSBuild Targets](./build/MSBUILD_COMMON_TARGETS.md) — standard MSBuild target execution order
- [Versioning](./versioning/VERSIONING.md) — the mono-repo fixed versioning strategy
- [References](./REFERENCES.md) — external MSBuild and tooling references
