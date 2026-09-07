# IMediaHubApplicationBuilder

Namespace: `Assimalign.Cohesion.MediaHub`
Assembly: `Assimalign.Cohesion.MediaHub`

## Purpose

`IMediaHubApplicationBuilder` is the public composition seam for a MediaHub application. It extends `IHostBuilder` while refining `Build()` to return `IMediaHubApplication`.

## Surface and behavior

- `Build()` creates a configured MediaHub application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.MediaHub.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.MediaHub;
using Assimalign.Cohesion.MediaHub.Hosting;

IMediaHubApplicationBuilder builder = MediaHubApplication.CreateBuilder(args);
await using IMediaHubApplication application = builder.Build();
```
