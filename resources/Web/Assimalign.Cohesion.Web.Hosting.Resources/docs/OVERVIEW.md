# Assimalign.Cohesion.Web.Hosting.Resources — Overview

## Purpose

Runs a Cohesion Web application as a managed resource instance without changing how the
application is composed: it publishes its manifest to the gateway that provisioned it, restores the
state the gateway kept, serves its control plane on a private listener, and exposes the settings
the application's features registered as resource actions. The design record is
[DESIGN.md](DESIGN.md).

## Scope

- In scope: the `AddResource` verb on `WebApplicationBuilder`, the actions over registered settings
  (the `cors` action over the `cors` setting), and the HTTP server for the control plane.
- Out of scope: the resource contracts and the resource service (Hosting.Resources), the settings
  themselves (each feature package and the Web root), gateways and application resources
  (ApplicationModel and its Gateway).

## Dependencies

Web, Web.Hosting, Web.Server, Web.Cors, Http, Hosting, and Hosting.Resources.

## Usage

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(cors => cors.Options.AddDefaultPolicy(policy => policy.WithOrigins("https://shop.example")));
builder.AddResource("orders-api");   // the only line that makes the application a resource

var app = builder.Build();
app.UseCors();                       // the policy the gateway's writes change
await app.RunAsync();
```

The provisioning gateway sets `COHESION_MANIFEST_ENDPOINT` and `COHESION_CONTROL_PLANE_ENDPOINT`.
Without the first, the application runs unmanaged with the settings its code defines.
