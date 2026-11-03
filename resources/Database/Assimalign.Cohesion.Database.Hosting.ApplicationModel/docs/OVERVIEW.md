# Assimalign.Cohesion.Database.Hosting.ApplicationModel — Overview

## Purpose

Runs an ordinary Database application as a managed resource instance: the selected platform client
publishes its manifest, restores gateway snapshots and carries calls to the actions the application
adds. Database construction and wire listeners remain ordinary application composition. See
[DESIGN.md](DESIGN.md) for ownership, lifecycle and known platform gaps.

## Scope

- The AddResource extension on builder.Services, DatabaseResourceOptions and the internal instance.
- Application-added actions through ApplicationInstanceOptions.AddAction, in registration order.
- No Database-owned actions or health contributors yet: catalog mutations need a design suited to
  snapshot restore, and gateway health aggregation remains an owner follow-up.
- Transports belong to platform packages; gateways and the logical model belong to ApplicationModel.Gateway.

## Dependencies

Database, Database.Hosting, DependencyInjection, Hosting and ApplicationModel.Hosting. The package
is a public App.Database framework member. The application references each platform it opts into.

## Usage

```csharp
DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);
builder.AddSql("orders-sql", sql =>
{
    sql.Options.RootPath = "data";
    sql.AddDatabase("orders");
    sql.AddServer(server => server.Listen(new Uri("cohesion-db://127.0.0.1:5740")));
});
builder.Services.AddResource("orders-db", resource => resource.UseInProcess());

await using DatabaseApplication application = builder.Build();
await application.RunAsync();
```

Without a platform named in the environment, AddResource registers nothing and the application
runs unmanaged. A named platform the resource did not opt into fails registration. Build opens,
recovers and provisions the engines before the host can publish; the gateway's startup window must
cover that work. Publication and snapshot restore precede service and server start. On stop,
servers drain before the instance's control plane closes.

The application adds an action with its source-generated JSON context, or creates it from the
application's services:

```csharp
builder.Services.AddResource("orders-db", resource => resource
    .UseInProcess()
    .AddAction(services => new RetentionAction(services.GetRequiredService<RetentionPolicy>()), OrdersJsonContext.Default));
```

A gateway declares it generically with `builder.AddResource("Database", "orders-db")`. The
in-process platform currently reports an HTTP endpoint serving Database health, including
/cohesion/v1/readyz and /cohesion/v1/livez for loopback peers. Hosting binds that platform http
assignment automatically when the runtime names a gateway. An unmanaged application opts in with
builder.AddHealthEndpoint(uri). Model servers use sql, documents, graph, kv or blob assignments
when their options omit a listener; an explicit Listen wins. The platform assigns only http today,
so dependents still configure the wire address as a literal setting. Gateway readiness currently
means publication, which precedes Database accept; see the design's open questions.
