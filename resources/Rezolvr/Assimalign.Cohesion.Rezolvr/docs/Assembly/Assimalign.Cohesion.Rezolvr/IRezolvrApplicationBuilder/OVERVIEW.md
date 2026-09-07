# IRezolvrApplicationBuilder

Namespace: `Assimalign.Cohesion.Rezolvr`
Assembly: `Assimalign.Cohesion.Rezolvr`

## Purpose

`IRezolvrApplicationBuilder` is the public composition seam for a Rezolvr application. It extends `IHostBuilder` while refining `Build()` to return `IRezolvrApplication`.

## Surface and behavior

- `Build()` creates a configured Rezolvr application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.Rezolvr.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.Rezolvr;
using Assimalign.Cohesion.Rezolvr.Hosting;

IRezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder(args);
await using IRezolvrApplication application = builder.Build();
```
