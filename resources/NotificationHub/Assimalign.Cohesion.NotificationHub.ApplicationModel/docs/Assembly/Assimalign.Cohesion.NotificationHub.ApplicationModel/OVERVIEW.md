# Public API

`NotificationHubResource` inherits `PlannedResource`; `NotificationHubResourceOptions` inherits `ResourceOptions`. `INotificationHubResourceDescriptor` exposes the typed resource and ordinary dependency edges. `AddNotificationHub` extends `IApplicationBuilder`; `NotificationHubResourceControlPlane.Create` returns an isolated `IResourceControlPlane` with no command kinds.
