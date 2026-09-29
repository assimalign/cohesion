# ConfigurationStore SDK Design

## Scope

The SDK chains through the base Cohesion SDK and supplies the
`Assimalign.Cohesion.App.ConfigurationStore` framework reference. The consumer owns an
ordinary executable and opts into orchestration with
`CohesionApplicationModel=enabled`. `Assimalign.Cohesion.Sdk.ApplicationModel` then
generates its manifest, resource accessors, and ConfigurationStore default-control-plane
registration.

## Manifest defaults

The resource kind is `ConfigurationStore`. Its default control plane is
`/cohesion/v1` on the `api` HTTPS endpoint, with persistent data at `/data` and
StatefulSet lifecycle defaults. Props declare items before the consumer body so
ordinary MSBuild `Update` and `Remove` remain available.

`CohesionCommand` advertises `configurationstore.add-namespace`,
`configurationstore.set-value`, and `configurationstore.remove-value`. The manifest task
writes the sorted command kinds as bare strings in `commands`; owners, keys, values, and
execution status belong to runtime application-model declarations. ConfigurationStore.ApplicationModel supplies the
`AddConfigurationStore` verb, the typed descriptor's command verbs, and the default
control plane; the gateway delivers the declared commands through its generic
`ResourceControlPlaneCommandClient`, so no ConfigurationStore.Client reference is needed
for commands. The SDK does not execute commands or introduce a second manifest shape.
None of the three kinds sets `RequiresInputResolver`, the `CohesionCommand` metadata that
writes an entry in the object form and makes a gateway's `Build()` require an input resolver.

## Verification

The ApplicationModel SDK package tests build an enabled ConfigurationStore executable
and assert its exact command array. Gateway SDK package tests compose a
ConfigurationStore target through the hand-written `AddConfigurationStore` verb and its
typed command verbs, assert that `Gateway.g.cs` carries its manifest and no generated
`Add<Member>` verb, and assert that no ConfigurationStore.Client package or
ConfigurationStore.Hosting assembly reaches the gateway.
