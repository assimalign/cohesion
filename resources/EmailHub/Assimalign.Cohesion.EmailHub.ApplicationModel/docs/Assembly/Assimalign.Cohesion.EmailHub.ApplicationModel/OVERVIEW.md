# Public API

`EmailHubResource` inherits `PlannedResource`; `EmailHubResourceOptions` inherits `ResourceOptions`. `IEmailHubResourceDescriptor` exposes the typed resource and ordinary dependency edges. `AddEmailHub` extends `IApplicationBuilder`; `EmailHubResourceControlPlane.Create` returns an isolated `IResourceControlPlane` with no command kinds.
