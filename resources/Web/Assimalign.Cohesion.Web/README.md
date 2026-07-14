# Assimalign.Cohesion.Web

The Web area root: the middleware-first pipeline and composition abstractions
(`IWebApplication*`, `IWebApplicationMiddleware`, `WebApplicationMiddleware`) that every
Web feature library builds against and that `Assimalign.Cohesion.Web.Hosting`
implements. The root carries no features of its own — feature libraries
(`Assimalign.Cohesion.Web.<Feature>`) compose against these seams and ship their own
builder verbs.

- [docs/OVERVIEW.md](docs/OVERVIEW.md) — scope, dependencies, usage
- [docs/DESIGN.md](docs/DESIGN.md) — the pipeline model and why the root stays this small
- [resources/Web/README.md](../README.md) — the area map and the build-enforced
  hosting-isolation rule
