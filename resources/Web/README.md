# Web Resource Area

The Web resource (`resources/Web/*`) is Cohesion's L3 web application platform: the composition
abstractions, the request-pipeline feature libraries, and the hosting runtime that together form
what the `Assimalign.Cohesion.Sdk.Web` SDK delivers through the `Assimalign.Cohesion.App.Web`
shared framework.

## The dependency rule

The area follows one structural rule, adopted 2026-07-10:

> **`Assimalign.Cohesion.Web.Hosting` is the runtime module — it is neither referenced by any
> Web-area library nor references any of them.** Feature libraries may reference the root
> `Assimalign.Cohesion.Web`, each other, and anything outside the Web area (`Http.*`,
> `Security.*`, `IdentityModel.*`, …). The one sanctioned exception is
> `Assimalign.Cohesion.Web.Testing`, which drives the concrete runtime and therefore references
> the hosting module (documented in its csproj and DESIGN.md).

Why the rule exists:

- **Nothing depends on Web.Hosting** because the hosting module composes DI, configuration,
  logging, and transport wiring. A feature library that referenced it would drag that whole
  composition surface into every consumer and push users toward container-driven design — the
  opposite of the repo's dependency-free feature-package philosophy.
- **Web.Hosting depends on no feature library** because the `App.Web` framework
  (`frameworks/Assimalign.Cohesion.App.props`) is what delivers the family to applications: an
  app using `Sdk.Web` sees every Web assembly without any project wiring, so the runtime never
  needs compile-time knowledge of the features it hosts. Builder verbs ship with their feature
  (`AddAuthentication` in Web.Authentication, `AddCookie` in Web.Authentication.Cookie,
  `AddJwtBearer` in Web.Authentication.Bearer, `AddRouting`/`UseRouting` in Web.Routing, …) and
  compose against the root project's `IWebApplicationBuilder`/`IWebApplicationPipelineBuilder`
  seams.

**The rule is build-enforced — centrally, for every resource area.** The Web rule is the local
instance of the repo-wide *resource hosting-isolation rule* in
`build/Targets/Build.Rules.targets` (prose: `.claude/rules/resource-areas.md`): each
`resources/<Area>/` ships one `Assimalign.Cohesion.<Area>.Hosting`, no library in the area may
reference it (`COHRES001`, checked against both the project-reference graph and the resolved
assembly closure), and the hosting module may directly reference no same-area library except the
area root (`COHRES002`). A project with a sanctioned, user-approved exception opts out
per-assembly via the `CohesionHostingIsolationExemptions` property in its own csproj —
`Web.Testing` declares the standing exemption this way. Test, example, and sample projects are
exempt — the rule constrains shipped libraries, not harnesses — and every Web project builds in
CI (`.github/workflows/resource-web.yml`) so the guard executes on each push.

## Adding a new Web feature library

A new `Assimalign.Cohesion.Web.<Feature>` project is not done until all of these are updated
(the working checklist also lives in `.claude/rules/web-area.md`):

1. **csproj** — references per the dependency rule; builder verbs (`Add<Feature>`/`Use<Feature>`)
   ship in the package itself, composing against the root `IWebApplicationBuilder` /
   `IWebApplicationPipelineBuilder` seams with dependency-free registration (typed features and
   values — no DI, no configuration binding).
2. **Framework manifest** — `frameworks/Assimalign.Cohesion.App.props`, `App.Web` group, plus any
   new outside-area transitive dependencies. Validate by packing
   `frameworks/Assimalign.Cohesion.App.Web.Runtime` (hard-fails on unresolvable assemblies).
3. **Solutions** — `resources/Web/Assimalign.Cohesion.Web.slnx` and the root
   `Assimalign.Cohesion.slnx`.
4. **CI** — the matrix in `.github/workflows/resource-web.yml`.
5. **Docs** — `docs/OVERVIEW.md` + `docs/DESIGN.md` (plus `docs/Assembly/` as the public API
   stabilizes), and a row in the project map below.

## Project map

| Project | Role |
| --- | --- |
| `Assimalign.Cohesion.Web` | The root: pipeline and composition abstractions (`IWebApplication*`, `WebApplicationMiddleware`) every library builds against |
| `Assimalign.Cohesion.Web.Hosting` | The runtime module: host, server, builder-time DI/config/logging composition |
| `Assimalign.Cohesion.Web.Routing` | Router, route patterns/constraints, endpoint metadata bag, link generation |
| `Assimalign.Cohesion.Web.Api` | Endpoint mapping sugar (`Map`/`MapGet`) over the router — the fluent `.Use(...)` / `IWebApplicationMiddleware` composition model |
| `Assimalign.Cohesion.Web.Serialization` | The content-serialization registry: media-type-keyed request-reader/response-writer halves, `AddJsonSerialization` over a source-generated resolver (AOT), and the `ReadContentAsync`/`WriteContentAsync` call sites |
| `Assimalign.Cohesion.Web.ProblemDetails` | The RFC 9457 problem+json payload (model + AOT-safe writer + `WriteProblemDetailsAsync`) |
| `Assimalign.Cohesion.Web.ErrorHandling` | The `OnError` fault seam: `AddErrorHandling().OnError(...)` handler chain + the terminal problem+json default (faults only — protocol outcomes stay in their features) |
| `Assimalign.Cohesion.Web.Query` | RFC 10008 QUERY server rules: request Content-Type validation / Accept-Query negotiation (400/415/406), method-preserving redirect helpers (307/308, 303), conditional QUERY (304/412) |
| `Assimalign.Cohesion.Web.HostFiltering` | Allowed-hosts enforcement (`UseHostFiltering`, register first): 400s requests whose transport-resolved host misses the allowlist |
| `Assimalign.Cohesion.Web.RequestTimeouts` | Request-timeout policies over the per-exchange abort primitive: global default + per-endpoint metadata, expiry → cancellation + configurable 504 |
| `Assimalign.Cohesion.Web.Authentication` / `.Cookie` / `.Bearer` | Scheme model + builder surface, and the handler packages that graft their scheme verbs onto it |
| `Assimalign.Cohesion.Web.Authorization` | Authorization contracts (in design) |
| `Assimalign.Cohesion.Web.ForwardedHeaders` | First-position forwarded-headers middleware (`UseForwardedHeaders`): proxy trust model over the core `Http` parsing primitives, publishing the `Http.Forwarded` effective-identity feature |
| `Assimalign.Cohesion.Web.CookiePolicy` / `.Cors` / `.Forms` / `.Sessions` / `.Health` | Request-pipeline feature libraries over their `Http.*` counterparts |
| `Assimalign.Cohesion.Web.StaticFiles` | Static file serving over an `IFileSystem` mount: conditional GET, single byte ranges, default documents, content-type mapping, precompressed `.br`/`.gz` negotiation |
| `Assimalign.Cohesion.Web.Diagnostics` | HTTP request/response logging middleware (field flags, allowlist redaction, bounded body capture) + the W3C/NCSA access-log file provider riding `Assimalign.Cohesion.Logging` |
| `Assimalign.Cohesion.Web.Testing` | In-memory test factory for the runtime (sanctioned Web.Hosting reference) |
| `Assimalign.Cohesion.Web.ApplicationModel` | Placeholder awaiting the ApplicationModel Phase-4 rebuild |

Layering: L3 platform. Everything here builds on the L1 protocol stack (`libraries/Http`,
`libraries/Connections`, `libraries/Security`). The L2 runtime/composition libraries
(`libraries/Hosting`, `libraries/DependencyInjection`, `libraries/Configuration`,
`libraries/Logging`) are consumed by the hosting module and by the `Web.Testing` harness (which
drives the runtime and resolves the server from its service provider) — never by the feature
libraries. (`Web.ApplicationModel`'s placeholder csproj still lists L2 references pending its
Phase-4 rebuild.)

Per-project documentation lives in each project's `docs/OVERVIEW.md` and `docs/DESIGN.md`.
