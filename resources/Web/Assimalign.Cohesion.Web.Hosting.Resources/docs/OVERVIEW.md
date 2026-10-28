# Assimalign.Cohesion.Web.Hosting.Resources — Overview

## Purpose

Runs a Cohesion Web application as a managed resource instance: it publishes its manifest to the
gateway that provisioned it, restores the state the gateway kept, serves its control plane on a
private listener, and exposes its CORS policy as a resource action. The design record is
[DESIGN.md](DESIGN.md).

## Scope

- In scope: the `AddResource` verb on `WebApplicationBuilder`, the `cors` action and its
  `UseCors(resource)` middleware, and the HTTP server for the control plane.
- Out of scope: the resource contracts and the resource service (Hosting.Resources), gateways and
  application resources (ApplicationModel and its Gateway).

## Dependencies

Web, Web.Hosting, Web.Server, Web.Cors, Http, Hosting, and Hosting.Resources.

## Usage

```csharp
var builder = WebApplication.CreateBuilder(args);
WebResource resource = builder.AddResource("orders-api", r => r.AddCors(new WebCorsPolicy
{
    Origins = ["https://shop.example"],
}));

var app = builder.Build();
app.UseCors(resource);   // the policy the gateway's writes change
await app.RunAsync();
```

The provisioning gateway sets `COHESION_MANIFEST_ENDPOINT` and `COHESION_CONTROL_PLANE_ENDPOINT`.
Without the first, the application runs unmanaged with the policy its code defines.
