# ApiManagerApplication

Namespace: `Assimalign.Cohesion.ApiManager.Hosting`
Assembly: `Assimalign.Cohesion.ApiManager.Hosting`

## Purpose

`ApiManagerApplication` is the public factory for the API manager hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IApiManagerApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder registers no hosted services.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.ApiManager;
using Assimalign.Cohesion.ApiManager.Hosting;

IApiManagerApplicationBuilder builder = ApiManagerApplication.CreateBuilder(args);
await using IApiManagerApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
