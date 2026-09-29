# Public API

`LoadBalancerResource` inherits `PlannedResource`; `LoadBalancerResourceOptions` inherits `ResourceOptions`. `ILoadBalancerResourceDescriptor` exposes the typed resource and ordinary dependency edges. `AddLoadBalancer` extends `IApplicationBuilder`; `LoadBalancerResourceControlPlane.Create` returns an isolated `IResourceControlPlane` with no command kinds.
