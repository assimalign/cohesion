# IEmailHubApplicationBuilder

Namespace: `Assimalign.Cohesion.EmailHub`
Assembly: `Assimalign.Cohesion.EmailHub`

## Purpose

`IEmailHubApplicationBuilder` is the public composition seam for an email hub application. It extends `IHostBuilder` while refining `Build()` to return `IEmailHubApplication`.

## Surface and behavior

- `Build()` creates a configured email hub application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.EmailHub.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.EmailHub;
using Assimalign.Cohesion.EmailHub.Hosting;

IEmailHubApplicationBuilder builder = EmailHubApplication.CreateBuilder(args);
await using IEmailHubApplication application = builder.Build();
```
