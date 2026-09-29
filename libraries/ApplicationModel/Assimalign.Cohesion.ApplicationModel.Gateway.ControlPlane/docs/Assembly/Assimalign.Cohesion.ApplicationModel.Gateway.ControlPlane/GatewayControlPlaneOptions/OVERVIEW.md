# GatewayControlPlaneOptions

Configures the optional Local metadata root, token-validation clock, and command dispatchers.
A dispatcher is registered for one exact resource kind, or for
`IGatewayResourceCommandClient.AnyKind` as the catch-all that serves every kind with no exact
registration. Each kind may be registered once. The factory validates and snapshots
registrations before serving.
