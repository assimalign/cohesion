# LogSpaceApplication

Namespace: `Assimalign.Cohesion.LogSpace.Hosting`
Assembly: `Assimalign.Cohesion.LogSpace.Hosting`

## Purpose

`LogSpaceApplication` is the public factory for the LogSpace hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `ILogSpaceApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder materializes its explicitly registered host services in registration order; no services are registered automatically.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`. Building throws `InvalidOperationException` when a registered service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.LogSpace;
using Assimalign.Cohesion.LogSpace.Hosting;

ILogSpaceApplicationBuilder builder = LogSpaceApplication.CreateBuilder(args);
await using ILogSpaceApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
