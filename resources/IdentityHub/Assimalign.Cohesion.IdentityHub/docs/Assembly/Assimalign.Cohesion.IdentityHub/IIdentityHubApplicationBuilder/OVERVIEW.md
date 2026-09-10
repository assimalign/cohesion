# IIdentityHubApplicationBuilder

Namespace: `Assimalign.Cohesion.IdentityHub`

Assembly: `Assimalign.Cohesion.IdentityHub`

`IIdentityHubApplicationBuilder` is the public code-first composition seam. `AddAudience` declares issuable audiences. `AddClient` registers a client whose options may enable a confidential client-credentials grant, the device grant, or both. `AddService` composes extra lifecycle services, and `Build` returns `IIdentityHubApplication`.

```csharp
IIdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder(args)
    .AddAudience("urn:example:api")
    .AddClient("worker", client =>
    {
        client.ClientSecret = configuration.ClientSecret;
        client.Audiences.Add("urn:example:api");
    });

await using IIdentityHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```

Duplicate clients or audiences are rejected. Build also rejects an undeclared client audience, an empty grant set, or an invalid token lifetime. The concrete builder hashes client secrets before retaining registrations.
