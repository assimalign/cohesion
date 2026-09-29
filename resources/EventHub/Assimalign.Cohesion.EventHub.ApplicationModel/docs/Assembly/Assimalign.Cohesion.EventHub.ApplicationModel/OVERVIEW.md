# Public API

`EventHubResource` inherits `PlannedResource`; `EventHubResourceOptions` inherits `ResourceOptions`. `IEventHubResourceDescriptor` exposes the typed resource and ordinary dependency edges. `AddEventHub` extends `IApplicationBuilder`; `EventHubResourceControlPlane.Create` returns an isolated `IResourceControlPlane` with no command kinds.
