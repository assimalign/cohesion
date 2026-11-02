# ApplicationModel.Hosting.InProcess

The instance half of the in-process platform. A resource opts into it in its area's builder:

```csharp
builder.Services.AddResource("orders-api", resource => resource.UseInProcess());
```

When a gateway runs the resource inside the gateway's own process
([ApplicationModel.Gateway.InProcess](../Assimalign.Cohesion.ApplicationModel.Gateway.InProcess/README.md),
the gateway half), the instance publishes to that gateway's in-process endpoint and attaches its
control plane there, so the gateway calls it directly, with no listener. On any other platform the
client is not used.

See [docs/OVERVIEW.md](docs/OVERVIEW.md) for usage and [docs/DESIGN.md](docs/DESIGN.md) for the
decisions.
