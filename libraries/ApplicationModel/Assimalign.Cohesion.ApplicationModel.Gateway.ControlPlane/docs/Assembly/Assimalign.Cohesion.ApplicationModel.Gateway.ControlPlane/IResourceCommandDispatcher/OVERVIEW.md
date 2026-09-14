# IResourceCommandDispatcher

The protocol-client boundary between a serving gateway and an area resource control plane.
Implementations identify one manifest resource kind and apply or delete the neutral
`ResourceCommand` envelope at the resource's observed default control-plane `System.Uri`. Each
dispatch receives the target resource's current bootstrap bearer credential from the serving
gateway.

`ApplyAsync` and `DeleteAsync` require a
`RemoteCertificateValidationCallback? serverCertificateValidator` parameter immediately before
the optional cancellation token. Apply it to the outbound transport. The serving gateway obtains
it from `IResourceTransportTrustProvider.CreateOutboundTrustValidator(application)` for HTTPS
targets, using its probe/store transport anchors. HTTP targets and applications without anchors
pass `null`, which retains platform default trust.
