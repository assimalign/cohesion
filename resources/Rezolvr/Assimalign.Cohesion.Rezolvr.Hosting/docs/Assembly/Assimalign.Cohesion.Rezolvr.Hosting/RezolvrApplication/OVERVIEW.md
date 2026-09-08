# RezolvrApplication

Namespace: `Assimalign.Cohesion.Rezolvr.Hosting`
Assembly: `Assimalign.Cohesion.Rezolvr.Hosting`

## Purpose

`RezolvrApplication` is the public factory for the Rezolvr hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IRezolvrApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder materializes its registered service factories once against the new Rezolvr context and preserves registration order; no services are added by default.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.Rezolvr;
using Assimalign.Cohesion.Rezolvr.Hosting;

IRezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder(args);
await using IRezolvrApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
