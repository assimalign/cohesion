# Assimalign.Cohesion.Web

The Web area root: the middleware-first pipeline and composition abstractions
(`IWebApplication*`, `IWebApplicationMiddleware`, `WebApplicationMiddleware`) that every
Web feature library builds against and that `Assimalign.Cohesion.Web.Hosting`
implements — plus the first-position forwarded-headers middleware
(`UseForwardedHeaders`), which resolves the effective client address/scheme/host behind
proxies under an explicit trust model and surfaces it as the `IHttpForwardedFeature`
from `Assimalign.Cohesion.Http`.

- [docs/OVERVIEW.md](docs/OVERVIEW.md) — scope, dependencies, usage
- [docs/DESIGN.md](docs/DESIGN.md) — the pipeline model, the forwarded-headers trust
  model, and the first-position ordering contract
- [resources/Web/README.md](../README.md) — the area map and the build-enforced
  hosting-isolation rule
