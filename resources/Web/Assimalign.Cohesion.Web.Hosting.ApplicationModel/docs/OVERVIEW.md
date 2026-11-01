# Assimalign.Cohesion.Web.Hosting.ApplicationModel — Overview

## Purpose

Runs a Cohesion Web application as a managed resource instance without changing how the
application is composed: through the client of the platform its gateway runs it on, it publishes
its manifest, restores the state the gateway kept, and lets the gateway run the resource's actions,
which expose the settings the application's features registered. The design record is
[DESIGN.md](DESIGN.md).

## Scope

- In scope: the `AddResource` verb on `builder.Services` and its `WebResourceOptions`, the
  resource's own actions over registered settings (the `cors` action over the `cors` setting), and
  the actions the application adds with the base options' `AddAction`.
- Out of scope: every transport, which belongs to the platform packages the resource opts into
  (such as ApplicationModel.Hosting.InProcess); the resource contracts and the resource service
  (ApplicationModel.Hosting); the settings themselves (each feature package and the Web root);
  gateways and application resources (ApplicationModel and its Gateway).

## Dependencies

Web, Web.Hosting, Web.Cors, Http, DependencyInjection, Hosting, and ApplicationModel.Hosting.

## Usage

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(cors => cors.Options.AddDefaultPolicy(policy => policy.WithOrigins("https://shop.example")));
builder.Services.AddResource("orders-api", resource => resource.UseInProcess());   // a resource on the in-process platform

var app = builder.Build();
app.UseCors();                       // the policy the gateway's writes change
await app.RunAsync();
```

The resource opts into each platform it runs on with that platform's verb. The gateway names the
platform an instance runs on in its environment; without one, the application runs unmanaged with
the settings its code defines, and a platform it did not opt into fails its build.

An application adds an action of its own with its JSON context, which declares the action's
content types:

```csharp
builder.Services.AddResource("orders-api", resource => resource
    .UseInProcess()
    .AddAction(services => new FlushCacheAction(services.GetRequiredService<OrderCache>()))
    .AddAction(new LogLevelAction(), OrdersJsonContext.Default));
```
