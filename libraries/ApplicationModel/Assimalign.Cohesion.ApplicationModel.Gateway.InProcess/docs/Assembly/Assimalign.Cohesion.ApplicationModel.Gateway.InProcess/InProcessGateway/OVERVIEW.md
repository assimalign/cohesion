# InProcessGateway

`InProcessGateway` is the sealed `ApplicationGateway` implementation for the `inprocess`
provider. Its default constructor uses `InProcessGatewayOptions` defaults; the options constructor
validates directory, timing, and restart values before realization begins.

At `Build()`, the gateway rejects plain executables, image/package-only resources, missing entry
bindings, non-composable manifests, and plans the local process cannot honor. At run time it starts
its `ProcessHost`, invokes members in dependency order, publishes loopback endpoints and lifecycle
state, and stops adopted hosts in reverse order.

The ordinary application-gateway lifetime stops the gateway and every adopted member. Callers
normally do not construct it directly; `Sdk.Gateway` provider selection does so from command-line
options.
