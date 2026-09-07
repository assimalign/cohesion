# IIdentityHubApplicationBuilder

Namespace: `Assimalign.Cohesion.IdentityHub`
Assembly: `Assimalign.Cohesion.IdentityHub`

## Purpose

`IIdentityHubApplicationBuilder` is the public composition seam for an identity hub application. It extends `IHostBuilder` while refining `Build()` to return `IIdentityHubApplication`.

## Surface and behavior

- `Build()` creates a configured identity hub application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.IdentityHub.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.IdentityHub;
using Assimalign.Cohesion.IdentityHub.Hosting;

IIdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder(args);
await using IIdentityHubApplication application = builder.Build();
```
