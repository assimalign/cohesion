# Assimalign.Cohesion.Web.Hosting

The Web runtime composes a concrete `WebApplication : Host<WebApplicationContext>`
through `WebApplication.CreateBuilder(args)`. It implements the root application and
builder contracts and integrates DI, configuration, logging, HTTP transports, and
the enabled resource's control plane at builder time.

## Composition and lifecycle

Feature verbs extend the root `IWebApplicationBuilder`. Background work is registered
through the concrete `WebApplicationBuilder.AddService` instance or context-factory
overload. Factories run once at `Build()`; services start in registration order before
servers and stop in reverse order after every server drains.

## Dependencies and hosting family

The module references only the Web root within its area (COHRES002), together with
Cohesion's hosting, configuration, DI, logging, and transport infrastructure. Its
`Hosting.Resources` and `Hosting.Health` integrations are runtime concerns; the
Web root references no hosting library. The reusable `Web.Hosting.Resources` and
`Web.Hosting.Health` packages do not reference this module. The internal control-plane
terminal remains here until the 31f same-area hosting-family follow-up.

All public composition is explicit and AOT-compatible. See [Design](DESIGN.md) for
listener ownership, cancellation, failure isolation, and control-plane behavior.
