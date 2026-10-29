# Assimalign.Cohesion.ApplicationModel.Definition

The logical side of the Cohesion application model: what each gateway deploys, and each gateway's
deployment of the resources it runs. The types it shares with running instances live in
`Assimalign.Cohesion.ApplicationModel`.

A gateway program composes its application with `Application.CreateBuilder(name, args)`, selects
the gateway that runs it, and builds it into an immutable model. The builder is the root application
set. An application set is one gateway's scope: the application resources it runs and the child gateways
it runs, each with a set of its own, so the sets form one hierarchy under the root gateway. A
resource declares the resources it depends on and the permissions it needs on each, which the
gateway registers with the tenant's identity provider. The root set projects the hierarchy as the
global manifest.

At run time, a gateway keeps one `ApplicationResourceDeployment` per resource it runs: it registers each
instance that publishes its manifest, answers with the state written since the gateway was deployed,
and names the instances that must restore each new write.

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
