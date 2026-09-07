# IApiManagerApplicationBuilder

Namespace: `Assimalign.Cohesion.ApiManager`
Assembly: `Assimalign.Cohesion.ApiManager`

## Purpose

`IApiManagerApplicationBuilder` is the public composition seam for an API manager application. It extends `IHostBuilder` while refining `Build()` to return `IApiManagerApplication`.

## Surface and behavior

- `Build()` creates a configured API manager application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.ApiManager.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.ApiManager;
using Assimalign.Cohesion.ApiManager.Hosting;

IApiManagerApplicationBuilder builder = ApiManagerApplication.CreateBuilder(args);
await using IApiManagerApplication application = builder.Build();
```
