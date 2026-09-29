# Assimalign.Cohesion.ApplicationModel.Gateway.InProcess

This namespace exposes the in-process gateway provider, its options, gateway-selection
extensions, and the SDK infrastructure bindings that the generated `Gateway.CreateBuilder(args)`
uses to make a member colocatable.

- `InProcessGateway` owns member entry invocation, adopted-host lifetime, state publication, and
  the in-process plan controller.
- `InProcessGatewayOptions` configures state storage, probing, liveness thresholds, and restart
  limits.
- `InProcessGatewayExtensions` selects the provider on an `IApplicationBuilder`.
- `InProcessResourceDescriptorExtensions` binds one built resource descriptor to its statically
  referenced executable assembly and isolated content root. No generated code calls it; it serves
  builders that are not created through `Gateway.CreateBuilder(args)`.
- `InProcessResourceManifestExtensions` binds a generated `Manifests.<Name>` manifest, by its
  application and resource names, to the same assembly and content root; the generated
  `Gateway.CreateBuilder(args)` registers one per enabled, composable project resource, so the
  binding applies whichever verb adds the resource.

Normal applications use the generated `Gateway.CreateBuilder(args)`, `Manifests`, and
`UseGateway(args)` surface, and add each resource through its area's hand-written verb, such as
`builder.AddWeb(Manifests.<Name>)`, or through `builder.AddResource(Manifests.<Name>)`. Both
bindings are hidden from IntelliSense because generated code supplies the required trimming root.
