# Public API

`LogSpaceResource` inherits `PlannedResource`; `LogSpaceResourceOptions` inherits `ResourceOptions`. `ILogSpaceResourceDescriptor` exposes the typed resource and ordinary dependency edges. `AddLogSpace` extends `IApplicationBuilder`; `LogSpaceResourceControlPlane.Create` returns an isolated `IResourceControlPlane` with no command kinds.
