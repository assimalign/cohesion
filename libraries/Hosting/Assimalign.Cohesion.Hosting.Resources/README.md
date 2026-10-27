# Assimalign.Cohesion.Hosting.Resources

What a Cohesion resource instance is, and the control plane that runs inside it.

A resource instance has an identity (its provider, its resource name, and a key of its own), the
actions its control plane exposes, the state those actions leave, and health contributors. When its
host starts, the control plane publishes the instance's manifest to the gateway that manages it and
restores the state the gateway returns: every stateful write made since the resource was last
deployed. Afterwards it executes actions and restores state on request.

The library is transport-neutral and gateway-neutral. The Web area binds the control plane to HTTP;
`Assimalign.Cohesion.ApplicationModel` and `Assimalign.Cohesion.ApplicationModel.Gateway` manage
instances.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
