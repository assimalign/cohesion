# IResourceCommandDispatcher

The protocol-client boundary between a serving gateway and an area resource control plane.
Implementations identify one manifest resource kind, or `IGatewayResourceCommandClient.AnyKind`
to serve every kind that has no exact-kind dispatcher. They apply or delete the neutral
`ResourceCommand` envelope at the resource's observed default control-plane `System.Uri`. Each
dispatch receives the serving gateway's current `ResourceAccess` bearer credential for the target
resource — minted through the application's registered credential issuer, or the default ES256
application-key token, which is the resource's bootstrap credential for the pass.

`ApplyAsync` and `DeleteAsync` require a
`RemoteCertificateValidationCallback? serverCertificateValidator` parameter immediately before
the optional cancellation token. Apply it to the outbound transport. The serving gateway obtains
it from `IResourceTransportTrustProvider.CreateOutboundTrustValidator(application)` for HTTPS
targets, using its probe/store transport anchors. HTTP targets and applications without anchors
pass `null`, which retains platform default trust.
