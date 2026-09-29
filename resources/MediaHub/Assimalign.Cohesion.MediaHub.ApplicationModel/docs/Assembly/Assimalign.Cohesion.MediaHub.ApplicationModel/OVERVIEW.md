# Public API

`MediaHubResource` inherits `PlannedResource`; `MediaHubResourceOptions` inherits `ResourceOptions`. `IMediaHubResourceDescriptor` exposes the typed resource and ordinary dependency edges. `AddMediaHub` extends `IApplicationBuilder`; `MediaHubResourceControlPlane.Create` returns an isolated `IResourceControlPlane` with no command kinds.
