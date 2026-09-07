# ILogSpaceApplicationBuilder

Namespace: `Assimalign.Cohesion.LogSpace`
Assembly: `Assimalign.Cohesion.LogSpace`

## Purpose

`ILogSpaceApplicationBuilder` is the public composition seam for a LogSpace application. It extends `IHostBuilder` while refining `Build()` to return `ILogSpaceApplication`.

## Surface and behavior

- `Build()` creates a configured LogSpace application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.LogSpace.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.LogSpace;
using Assimalign.Cohesion.LogSpace.Hosting;

ILogSpaceApplicationBuilder builder = LogSpaceApplication.CreateBuilder(args);
await using ILogSpaceApplication application = builder.Build();
```
