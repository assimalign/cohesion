# InProcessGatewayExtensions

`InProcessGatewayExtensions` adds `UseInProcessGateway()` overloads to `IApplicationBuilder`.
The parameterless overload selects default options. The configuring overload creates an
`InProcessGatewayOptions` instance, applies the callback, and selects the resulting gateway.

Generated gateway applications normally call `UseGateway(args)` so the `--gateway inprocess`
selection and command-line state options are applied consistently. The direct extensions remain
useful for explicit programmatic composition and tests.
