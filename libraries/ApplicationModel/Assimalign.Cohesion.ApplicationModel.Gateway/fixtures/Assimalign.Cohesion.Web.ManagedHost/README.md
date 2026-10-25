# Assimalign.Cohesion.Web.ManagedHost

An executable acceptance fixture for the deployment-record management API. It is a real
`Web.Hosting` program that `ApplicationModel.Gateway.Tests` launches under a Local gateway: the
tests describe it with `COHESION_DESCRIBE_PATH`, as the SDK's describe step does, deploy it with
a deployment layer, change it through the management API, and observe the change at
`GET /greeting`.

Its runtime surface:

| Entry | Kind | Notes |
| --- | --- | --- |
| `Fixture:Greeting` | Live section | Applied without a restart; `GET /greeting` returns the value in force |
| `Fixture:Pool` | Restart section | A change reports `restartRequired` until the process restarts |
| `Fixture:Schema` | Backend, claimable section | Refuses overrides; accepts claims |
| `echo` | Every-instance action | Returns its input text |

Not packable; it exists only to be compiled from source against the current tree.
