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

## Project map

| Project | Role |
| --- | --- |
| `Assimalign.Cohesion.Web` | The root: pipeline and composition abstractions (`IWebApplication*`, `WebApplicationMiddleware`) every library builds against |
| `Assimalign.Cohesion.Web.Hosting` | The runtime module: host, server, builder-time DI/config/logging composition |
| `Assimalign.Cohesion.Web.Routing` | Router, route patterns/constraints, endpoint metadata bag, link generation |
| `Assimalign.Cohesion.Web.Api` / `.Api.Controllers` / `.Functions` | Endpoint mapping and (planned) controller/function binding surfaces |
| `Assimalign.Cohesion.Web.Results` / `.Results.ServerSentEvents` | The `IResult` deferred-response foundation and its SSE adapter |
| `Assimalign.Cohesion.Web.Authentication` / `.Cookie` / `.Bearer` | Scheme model + builder surface, and the handler packages that graft their scheme verbs onto it |
| `Assimalign.Cohesion.Web.Authorization` | Authorization contracts (in design) |
| `Assimalign.Cohesion.Web.CookiePolicy` / `.Cors` / `.Forms` / `.Sessions` / `.Health` | Request-pipeline feature libraries over their `Http.*` counterparts |
| `Assimalign.Cohesion.Web.Testing` | In-memory test factory for the runtime (sanctioned Web.Hosting reference) |
| `Assimalign.Cohesion.Web.ApplicationModel` | Placeholder awaiting the ApplicationModel Phase-4 rebuild |

Layering: L3 platform. Everything here builds on the L1 protocol stack (`libraries/Http`,
`libraries/Connections`, `libraries/Security`) and, in the hosting module only, the L2
runtime/composition libraries (`libraries/Hosting`, `libraries/DependencyInjection`,
`libraries/Configuration`, `libraries/Logging`).

Per-project documentation lives in each project's `docs/OVERVIEW.md` and `docs/DESIGN.md`.
