# Web area control-plane integration

Web.ControlPlane is a Web feature over the root pipeline seam. It references Hosting.Resources, Hosting.Health, and IdentityModel.Token.JsonWebToken, and never Web.Hosting. Hosting-isolation COHRES002 also prevents Web.Hosting from referencing this feature. The runtime's existing terminal therefore remains unchanged; a real Program test factory provides protocol parity coverage without another InternalsVisibleTo or guard exemption.

The feature is public in App.Web and private in each generic resource framework. Area ApplicationModel packages remain NuGet-only. Cross-area hosting implementation references use the coordinated CohesionPrivateProjectReference / CohesionFrameworkPrivateAssembly pair. The feature owns authentication and AOT-safe JSON; each area host owns its listener, readiness gate, and lifecycle.

See [the package design](../Assimalign.Cohesion.Web.ControlPlane/docs/DESIGN.md) for the exact protocol and the private response-completion seam limitation.
