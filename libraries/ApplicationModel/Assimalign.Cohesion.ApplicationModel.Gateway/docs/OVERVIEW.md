# Application gateway overview

The gateway realizes an immutable application graph in dependency order, waits for each target's
readiness gate, applies its resource commands, and then admits dependents. Runtime stop preserves
owned declarations; teardown removes commands and realized resources in reverse order.

Database and ConfigurationStore command delivery uses `ApplicationGatewayOptions.CommandClients`.
Replace a kind's registration to use another `IGatewayResourceCommandClient`. In-process hosts
expose their registered control plane directly; remote commands use the peer gateway client.
Command outcomes are available through `IApplicationResourceStateManager.GetCommandObservations`
and exported with provider detail. Required rejection blocks dependent startup.

See [DESIGN.md](DESIGN.md) for dependency boundaries, ownership, credential restrictions, and
the scope of commands-only model replacement.
