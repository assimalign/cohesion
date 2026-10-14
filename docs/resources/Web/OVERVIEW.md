# Web area overview

Web.Hosting includes reusable resource-management middleware. Thirteen other area hosting modules
consume the module privately to expose readiness, liveness, health, endpoints, stop, and command
envelopes. Web applications receive its public pipeline extension through App.Web, in the unchanged
`Assimalign.Cohesion.Web.Hosting.Resources` namespace.

Web.Hosting also adapts Hosting.Health contributors onto Web.Health's builder, in the unchanged
`Assimalign.Cohesion.Web.Hosting.Health` namespace. Web.Health retains the health model and HTTP
endpoints without a hosting-library reference. The integrations ship with Web.Hosting; their
former separate packages are removed (owner decision 57, 2026-10-13).

- [Area project map](../../../resources/Web/README.md)
- [Control-plane overview](../../../resources/Web/Assimalign.Cohesion.Web.Hosting/docs/OVERVIEW.md#resource-control-plane)
- [Control-plane design](../../../resources/Web/Assimalign.Cohesion.Web.Hosting/docs/DESIGN.md#resource-control-plane)
- [Health integration](../../../resources/Web/Assimalign.Cohesion.Web.Hosting/docs/DESIGN.md#health-integration)
