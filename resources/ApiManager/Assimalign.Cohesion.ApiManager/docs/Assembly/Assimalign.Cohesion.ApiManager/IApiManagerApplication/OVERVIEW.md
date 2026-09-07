# IApiManagerApplication

Namespace: `Assimalign.Cohesion.ApiManager`
Assembly: `Assimalign.Cohesion.ApiManager`

## Purpose

`IApiManagerApplication` is the public lifecycle contract for an API manager application. It extends `IHost` and adds the run-until-shutdown operation used by executable resources.

## Surface

- `RunAsync(CancellationToken cancellationToken = default)` starts the host, waits for shutdown, and completes after the host has stopped.
- The inherited `IHost` members expose the host identity, context, start, stop, and disposal lifecycle.

The current filler implementation registers no hosted services. A token that is already cancelled still drives the host through a clean start-and-stop transition; cancellation after startup requests graceful shutdown.

## Exceptions

`RunAsync` throws `ObjectDisposedException` when invoked after the application has been disposed. Lifecycle failures are propagated to the caller.

## Usage

```csharp
using Assimalign.Cohesion.ApiManager;
using Assimalign.Cohesion.ApiManager.Hosting;

await using IApiManagerApplication application =
    ApiManagerApplication.CreateBuilder(args).Build();

await application.RunAsync(cancellationToken);
```
