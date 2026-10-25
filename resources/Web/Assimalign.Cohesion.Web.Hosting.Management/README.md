# Web hosting management

Opt-in deployment-record configuration sections for Web features, starting with live CORS policies.
This package is NuGet-only: it is not a member of App.Web or of the private Web.Hosting closure the
other resource frameworks carry. Add it explicitly when a Web resource lets its gateway change a
feature's configuration without a redeploy.

```csharp
CorsPolicyStore cors = CorsPolicyStore.Create(configuredCors);
builder.Services.AddSingleton<ResourceConfigurationSection>(cors.AsResourceConfiguration());
WebApplication application = builder.Build();
application.UseDynamicCors(cors);
```

`AsResourceConfiguration()` creates the live `Web:Cors` section over the store; its baseline is the
store's configuration when it is called, so call it after configuring the store. The deployment
record's layers then replace the store's policies while the resource runs, and a value the store
refuses leaves every published policy in force and reports why in the resource's configuration status.

See [design](docs/DESIGN.md) and [overview](docs/OVERVIEW.md). Tests run with
`dotnet test resources/Web/Assimalign.Cohesion.Web.Hosting.Management/tests/Assimalign.Cohesion.Web.Hosting.Management.Tests.csproj`.
