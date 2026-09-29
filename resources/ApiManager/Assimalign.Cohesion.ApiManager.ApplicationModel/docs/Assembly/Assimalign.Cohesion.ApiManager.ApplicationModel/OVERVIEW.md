# Public API

`ApiManagerResource` inherits `PlannedResource`; `ApiManagerResourceOptions` inherits `ResourceOptions`. `IApiManagerResourceDescriptor` exposes the typed resource and ordinary dependency edges. `AddApiManager` extends `IApplicationBuilder`; `ApiManagerResourceControlPlane.Create` returns an isolated `IResourceControlPlane` with no command kinds.
