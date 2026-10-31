# Assimalign.Cohesion.ApplicationModel

The contracts of the Cohesion application model: what a resource's definition, its deployment by a
gateway, and its running instances share. That is the resource's identity, the manifest an instance
publishes, its actions' invocation and result types, the snapshots of their state, and the seam an
instance publishes through.

The logical side builds on it in `Assimalign.Cohesion.ApplicationModel.Definition`, and the physical
side in `Assimalign.Cohesion.ApplicationModel.Hosting`. Neither references the other.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
