# Assimalign.Cohesion.ApplicationModel.Hosting

The physical side of the Cohesion application model: what a running resource instance is, and the
control plane that runs inside it. Formerly `Assimalign.Cohesion.Hosting.Resources`; the types an
instance shares with its gateway, such as its manifest and action results, live in
`Assimalign.Cohesion.ApplicationModel.Protocol`, and its identity in `Assimalign.Cohesion.ApplicationModel`.

A resource instance has an identity (its provider, its resource name, and a key of its own), the
actions its control plane exposes, the state those actions leave, and health contributors. When its
host starts, the control plane publishes the instance's manifest to the gateway that manages it and
restores the state the gateway returns: every stateful write made since the resource was last
deployed. Afterwards it executes actions and restores state on request.

The library is transport-neutral and gateway-neutral. A platform's instance half supplies the client
that carries the manifest out and the gateway's calls in (`IApplicationInstanceGatewayClient`), and a resource
opts into each platform through its area's options (`ApplicationInstanceOptions`);
`Assimalign.Cohesion.ApplicationModel.Definition` and `Assimalign.Cohesion.ApplicationModel.Gateway`
manage instances without referencing this library.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
