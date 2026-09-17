# ConfigurationStore SDK Design

## Scope

The SDK chains through the base Cohesion SDK and supplies the
`Assimalign.Cohesion.App.ConfigurationStore` framework reference. The consumer owns an
ordinary executable and opts into orchestration with
`CohesionApplicationModel=enabled`. The base SDK then generates its manifest, resource
accessors, and ConfigurationStore default-control-plane registration.

## Manifest defaults

The resource kind is `ConfigurationStore`. Its default control plane is
`/cohesion/v1` on the `api` HTTPS endpoint, with persistent data at `/data` and
StatefulSet lifecycle defaults. Props declare items before the consumer body so
ordinary MSBuild `Update` and `Remove` remain available.

`CohesionCommand` advertises `configurationstore.set-value` and
`configurationstore.remove-value`. The base task writes the sorted command kinds as
bare strings in `commands`; owners, keys, values, and execution status belong to runtime
application-model declarations. ConfigurationStore.ApplicationModel supplies typed
descriptor verbs and the default control plane; ConfigurationStore.Client delivers
commands. The SDK does not execute commands or introduce a second manifest shape.

## Verification

The base SDK package tests build an enabled ConfigurationStore executable and assert
its exact command array. Gateway SDK package tests compile the generated typed
ConfigurationStore descriptor and command verbs and verify that a target advertising
commands requires ConfigurationStore.Client even without mount-source references.
