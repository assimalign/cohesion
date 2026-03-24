# Assimalign.Cohesion.Hosting Design

## Design Intent

The package splits host runtime concerns into explicit contracts: a host orchestrates lifecycle, a context carries state, and hosted services implement the start and stop behavior. That keeps runtime composition readable and testable.

## Architecture

- Host<TContext> is the lifecycle coordinator that starts, stops, and tracks hosted services.
- HostContext and IHostEnvironment isolate runtime state from the host implementation itself.
- BackgroundService is the convenience base class for long-running units of work inside a host.

## Layout Example

```text
Assimalign.Cohesion.Hosting/
  src/
    Assimalign.Cohesion.Hosting.csproj
    Abstractions/
    Exceptions/
    Extensions/
    Implementation/
    Internal/
    Properties/
    ValueObjects/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Implement a long-running hosted service

```csharp
internal sealed class Worker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

## Example 2: Run a host or expose it as a service

```csharp
Host<MyHostContext> host = CreateHost();

await host.RunAsync(cancellationToken);
IHostService service = host.AsService();
```
