# IIdentityHubApplicationBuilder

Namespace: `Assimalign.Cohesion.IdentityHub`

Assembly: `Assimalign.Cohesion.IdentityHub`

`IIdentityHubApplicationBuilder` is the public code-first composition seam. `AddAudience` declares issuable audiences. `AddClient` registers a client whose options may enable a confidential client-credentials grant, the device grant, or both. `Build` returns `IIdentityHubApplication`.

```csharp
IdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder(args);
builder.AddAudience("urn:example:api");
builder.AddClient("worker", client =>
    {
        client.ClientSecret = configuration.ClientSecret;
        client.Audiences.Add("urn:example:api");
    });

await using IdentityHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```

Duplicate clients or audiences are rejected. Build also rejects an undeclared client audience, an empty grant set, or an invalid token lifetime. The concrete builder hashes client secrets before retaining registrations.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IIdentityHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IIdentityHubApplicationContext` exposes `ContentRootPath`. `IIdentityHubApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`IdentityHubApplication.CreateBuilder(args)` returns the public concrete `IdentityHubApplicationBuilder`; its `Build()` returns the public `IdentityHubApplication : Host<IdentityHubApplicationContext>`. The public `IdentityHubApplicationContext` implements `IIdentityHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
