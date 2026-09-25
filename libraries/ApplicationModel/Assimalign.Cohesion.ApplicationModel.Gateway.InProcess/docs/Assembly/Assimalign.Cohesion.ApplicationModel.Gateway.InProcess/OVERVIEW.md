# Assimalign.Cohesion.ApplicationModel.Gateway.InProcess

This namespace exposes the in-process gateway provider, its options, gateway-selection
extensions, and the SDK infrastructure binding used by generated resource verbs.

- `InProcessGateway` owns member entry invocation, adopted-host lifetime, state publication, and
  the in-process plan controller.
- `InProcessGatewayOptions` configures state storage, probing, liveness thresholds, and restart
  limits.
- `InProcessGatewayExtensions` selects the provider on an `IApplicationBuilder`.
- `InProcessResourceDescriptorExtensions` binds an SDK-generated resource descriptor to its
  statically referenced executable assembly and isolated content root.
- `InProcessResourceManifestExtensions` binds a generated resource manifest, by its application
  and resource names, to the same assembly and content root; the generated
  `Gateway.CreateBuilder(args)` registers one per enabled, composable project resource.

Normal applications should use the generated `Gateway.CreateBuilder(args)`, resource verbs, and
`UseGateway(args)` surface. Both bindings are hidden from IntelliSense because generated code
supplies the required trimming root.
