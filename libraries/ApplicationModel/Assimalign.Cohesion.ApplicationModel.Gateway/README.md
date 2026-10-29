# Assimalign.Cohesion.ApplicationModel.Gateway

The Cohesion application gateway: it runs one gateway's application model on a platform.

A gateway deploys its own resources through an infrastructure provider such as Kubernetes or
Docker. Each deployed resource's instances publish their manifests to the gateway, which answers with
the state they must restore and tears down a resource that never publishes within the startup
window. The provider also supplies infrastructure actions, such as scaling, which the gateway exposes
on each resource.

Each gateway is deployed by its own pipeline. A child gateway joins its parent, registers with it
under a lease, and fills its settings in from the parent's outputs; the parent knows nothing of a
child before it joins. A gateway program declares its model and configures its gateway on one
`ApplicationBuilder`. The instances' publications reach the gateway through its transports, such as
the in-process channel of Gateway.InProcess.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
