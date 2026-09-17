# Public API

`MessageHubResource` inherits `PlannedResource`; `MessageHubResourceOptions` inherits `ResourceOptions`. `IMessageHubResourceDescriptor` exposes the typed resource and ordinary dependency edges. `AddMessageHub` extends `IApplicationBuilder`; `MessageHubResourceControlPlane.Create` returns an isolated `IResourceControlPlane` with no command kinds.
