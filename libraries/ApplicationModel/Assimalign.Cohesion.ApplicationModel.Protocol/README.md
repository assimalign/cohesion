# Assimalign.Cohesion.ApplicationModel.Protocol

What a Cohesion gateway and the instances of its resources exchange: the manifest an instance
publishes and the publication its gateway answers with, action invocations, their results and the
state snapshots they leave, the publisher seam both sides use, and the JSON content serializer
that writes it all. It builds on the identity in `Assimalign.Cohesion.ApplicationModel`.

The gateway (`Assimalign.Cohesion.ApplicationModel.Gateway`) and the instance runtime
(`Assimalign.Cohesion.ApplicationModel.Hosting`) reference it; the logical model
(`Assimalign.Cohesion.ApplicationModel.Definition`) never does.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
