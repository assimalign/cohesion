# Assimalign.Cohesion.ApplicationModel.Definition

The logical side of the Cohesion application model: what one gateway runs, declared as a model.
The types it shares with running instances live in `Assimalign.Cohesion.ApplicationModel`.

Each gateway declares its own model with `ApplicationModel.CreateBuilder(name)`: the application
resources it runs, the dependencies between them with the permissions each needs, their settings,
and, for a child gateway, the parent it joins. A setting may use an output of the gateway's own
resources or of its parent's. Building validates the declarations into an immutable model, which a
gateway runs (`Assimalign.Cohesion.ApplicationModel.Gateway`). A gateway's deployment of a resource
and the gateway itself live there.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
