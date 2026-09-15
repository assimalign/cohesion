# Web area overview

Web.Hosting.Resources adds reusable resource-management middleware to the Web hosting family. The twelve generic resource Hosting modules consume it privately to expose readiness, liveness, health, endpoints, stop, and command envelopes. Web applications receive its public pipeline extension through App.Web.

Web.Hosting.Health adapts Hosting.Health contributors onto Web.Health's builder. Web.Health
retains the health model and HTTP endpoints without a hosting-library reference.

- [Area project map](../README.md)
- [Control-plane overview](../Assimalign.Cohesion.Web.Hosting.Resources/docs/OVERVIEW.md)
- [Control-plane design](../Assimalign.Cohesion.Web.Hosting.Resources/docs/DESIGN.md)
