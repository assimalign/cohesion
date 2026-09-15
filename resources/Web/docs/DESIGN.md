# Web area control-plane integration

Web.Hosting.Resources is a hosting-family integration over the root pipeline seam. It references Hosting.Resources, Hosting.Health, and IdentityModel.Token.JsonWebToken, and never Web.Hosting. COHRES001 prevents roots and features from consuming this integration; COHRES002 also prevents Web.Hosting from referencing it. The runtime's existing terminal therefore remains unchanged; a real Program test factory provides protocol parity coverage without another InternalsVisibleTo or guard exemption.

The integration is public in App.Web and private in each generic resource framework. Area ApplicationModel packages remain NuGet-only. Cross-area hosting implementation references use the coordinated CohesionPrivateProjectReference / CohesionFrameworkPrivateAssembly pair. The integration owns authentication and AOT-safe JSON; each area host owns its listener, readiness gate, and lifecycle.

Web.Hosting.Health integrates Hosting.Health contributors with the independent Web.Health model.
It has no production consumer in this slice; Database.Hosting retains its existing mapping.

See [the package design](../Assimalign.Cohesion.Web.Hosting.Resources/docs/DESIGN.md) for the exact protocol and the private response-completion seam limitation.
