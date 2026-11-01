# Assimalign.Cohesion.ApplicationModel

The identity of the Cohesion application model: the names a resource's definition, its deployment
by a gateway, and its running instances all share. That is the resource's provider and name, the
ids derived from them, and the name of the platform it runs on.

What a gateway and an instance exchange builds on it in `Assimalign.Cohesion.ApplicationModel.Protocol`.
The logical side builds on it in `Assimalign.Cohesion.ApplicationModel.Definition`, and the instance
runtime on Protocol in `Assimalign.Cohesion.ApplicationModel.Hosting`. Definition and Hosting never
reference each other.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
