# Assimalign.Cohesion.Hosting Design

## Design Intent

The package splits host runtime concerns into explicit contracts: a host orchestrates lifecycle, a context carries state, and hosted services implement the start and stop behavior. That keeps runtime composition readable and testable.

## Architecture

- Host<TContext> is the lifecycle coordinator that starts, stops, and tracks hosted services. It is threading-neutral: starting and stopping are pure await state machines that spawn no threads and install no SynchronizationContext or TaskScheduler.
- HostContext and IHostEnvironment isolate runtime state from the host implementation itself.
- BackgroundService is the convenience base class for long-running units of *asynchronous* work inside a host. It is pool-scheduled: `StartAsync` launches `ExecuteAsync` directly and stores the real work task (no `Task.Factory.StartNew` wrapper, no `LongRunning`), so `StopAsync` cancels and then joins that exact task within the caller's shutdown budget, and any fault thrown by `ExecuteAsync` surfaces to the host (synchronous faults during start, post-yield faults on stop) instead of being swallowed. A cooperative cancellation exit is treated as a clean stop.

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
