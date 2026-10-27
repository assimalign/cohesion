# Assimalign.Cohesion.ApplicationModel.Gateway

The Cohesion application gateway: it runs an application model on a platform.

A gateway deploys its part of the hierarchy (its own resources and its child gateways) through an
infrastructure provider such as Kubernetes or Docker. Each deployed resource's instances publish
their manifests to the gateway, which answers with the state they must restore and tears down a
resource that never publishes within the startup window. The provider also supplies infrastructure
actions, such as scaling, which the gateway exposes on each resource.

A gateway starts from a deployment manifest: what it deploys, as the whole hierarchy plus its own
name, and where, as settings its provider reads.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
