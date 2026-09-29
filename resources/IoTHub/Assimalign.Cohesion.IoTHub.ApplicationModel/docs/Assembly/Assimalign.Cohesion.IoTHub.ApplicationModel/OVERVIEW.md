# Public API

`IoTHubResource` inherits `PlannedResource`; `IoTHubResourceOptions` inherits `ResourceOptions`. `IIoTHubResourceDescriptor` exposes the typed resource and ordinary dependency edges. `AddIoTHub` extends `IApplicationBuilder`; `IoTHubResourceControlPlane.Create` returns an isolated `IResourceControlPlane` with no command kinds.
