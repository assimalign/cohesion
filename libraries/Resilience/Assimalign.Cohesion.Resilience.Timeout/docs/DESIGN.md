# Assimalign.Cohesion.Resilience.Timeout Design

## Design Intent

The timeout package follows the same narrow strategy-package model as retry: keep the core pipeline generic, and put timeout-specific configuration and execution behavior in a separate assembly.

## Architecture

- TimeoutStrategyOptions owns fixed and dynamic timeout selection.
- TimeoutResilienceExtensions attaches the strategy to pipeline builders.
- TimeoutRejectedException keeps timeout failures explicit at the strategy boundary.

## Execution and timer ownership

The strategy rents and arms a cancellation source before invoking the callback. Its token is linked
to the caller's token, and a wrapper context carries the execution token without mutating the caller's
context. The source remains leased until the callback finishes, even when the pipeline has already
returned a timeout rejection.

A successfully completed `ValueTask` is consumed directly, without converting the outcome to a
`Task` or allocating a separate deadline race. This also covers synchronously captured callback
failures: the typed and void callback adapters represent exceptions as outcome values. Timeout
cancellation is still classified after consuming the outcome, so the fast path preserves rejection
and `OnTimeout` behavior when the cancellation source fired during an inline callback.

Incomplete callbacks use `Task.WaitAsync(timeout, TimeProvider)` to enforce the hard deadline.
The wait disposes its own timer when the callback or deadline wins. The previous `Task.Delay` /
`Task.WhenAny` race left its delay timer pending after successful completion, retaining one timer
per call for the full timeout budget. The cancellation source's separate timer remains reusable
through the existing pool and is disarmed on return.

When the deadline wins, the strategy cancels the execution token and rejects promptly even if the
callback ignores cancellation. A late-completion observer consumes its eventual outcome and owns
the caller registration and source until then; a later execution cannot reset that callback's token.
Parent cancellation remains a callback cancellation outcome rather than a timeout notification.
The callback adapters also keep a callback-thrown `TimeoutException` distinct from a deadline
raised by `WaitAsync`. No public contract or timeout option changes are required.

## Layout Example

```text
Assimalign.Cohesion.Resilience.Timeout/
  src/
    Assimalign.Cohesion.Resilience.Timeout.csproj
    Exception/
    Extensions/
    Internal/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Attach a fixed timeout to the pipeline

```csharp
IResiliencePipeline pipeline = new ResiliencePipelineBuilder()
    .UseTimeout(options =>
    {
        options.Timeout = TimeSpan.FromSeconds(2);
    })
    .Build();
```

## Example 2: Select timeout dynamically

```csharp
IResiliencePipeline pipeline = new ResiliencePipelineBuilder()
    .UseTimeout(options =>
    {
        options.TimeoutGenerator = args => ValueTask.FromResult(TimeSpan.FromSeconds(5));
    })
    .Build();
```
