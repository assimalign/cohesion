# Assimalign.Cohesion.Web.Routing — Overview

Selects the endpoint for an HTTP request and exposes its route values and metadata to the Web
pipeline. It provides deterministic matching and outbound link generation from one route table.

## Scope

The package parses route patterns, applies parameter constraints and typed conversions, composes
route groups and endpoint metadata, selects host-constrained routes, and generates links for named
routes or explicit route values. Matching distinguishes 404 from 405 with `Allow`, and supports
HEAD through GET routes.

`UseRouting` selects an endpoint; the pipeline's terminal runs it after subsequent middleware.
Routing owns that endpoint contract and terminal, plus non-rejoining `Map(path)` and `MapWhen`
branches and their path-base feature. Typed handler binding and content negotiation live in
`Web.Api`; authorization and other endpoint policies are supplied by their feature packages.

## Dependencies

- `Assimalign.Cohesion.Web` supplies application and pipeline composition contracts.
- `Assimalign.Cohesion.Http.Forwarded` supplies the effective host used for host-constrained matching.

The library has no hosting dependency. `Web.Hosting` consumes it to compose the runtime, and
`App.Web` makes it available to `Sdk.Web` applications. See the [Web area map](../../README.md).

## Usage

Register the per-application route table with `builder.Services.AddRouting()`, then call
`app.UseRouting()` while composing the application's pipeline. Map routes before application
startup through the returned `IRouterBuilder`, or through `Web.Api`'s `app.MapGet` and related
verbs. `MapGroup` shares a prefix and metadata; `WithName` enables named links and `RequireHost`
constrains selection. A host constraint selects a route; use `Web.HostFiltering` for a host allowlist.

Place middleware that reads endpoint metadata, such as authorization, CORS and rate limiting,
after `UseRouting`. A handler reads the match through `context.GetRouteMatch()` and metadata
through `context.GetEndpointMetadata<T>()`. Resolve `context.GetLinkGenerator()` to generate URLs
from the same route table. Register trusted forwarded-header processing before routing when
host selection must use the public host behind a proxy.

The route table is built and frozen when the pipeline starts; invalid templates and duplicate
route names fail during setup or startup. Each application owns its own table. Standalone users
can compose a `RouterBuilder` and work with the resulting `IRouter` directly, without a host.

See [DESIGN.md](DESIGN.md) for matching precedence, policy extension points, metadata composition,
link-generation rules, lifecycle and NativeAOT guarantees.
