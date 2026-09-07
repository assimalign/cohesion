# MediaHubApplication

Namespace: `Assimalign.Cohesion.MediaHub.Hosting`
Assembly: `Assimalign.Cohesion.MediaHub.Hosting`

## Purpose

`MediaHubApplication` is the public factory for the MediaHub hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IMediaHubApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder registers no hosted services.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.MediaHub;
using Assimalign.Cohesion.MediaHub.Hosting;

IMediaHubApplicationBuilder builder = MediaHubApplication.CreateBuilder(args);
await using IMediaHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
