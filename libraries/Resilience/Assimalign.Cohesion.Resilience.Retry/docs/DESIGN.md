# Assimalign.Cohesion.Resilience.Retry Design

## Design Intent

The retry package is one of the clearest examples of the intended resilience package model: the core assembly owns pipeline composition, and this assembly contributes one well-scoped strategy plus its options.

## Architecture

- RetryStrategyOptions carries retry count, delay, jitter, predicate, and callback behavior.
- RetryResilienceExtensions adds the strategy to both generic and non-generic pipeline builders.
- Internal strategy types keep execution details out of the public options surface.

## Layout Example

```text
Assimalign.Cohesion.Resilience.Retry/
  src/
    Assimalign.Cohesion.Resilience.Retry.csproj
    Extensions/
    Internal/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Attach retry behavior to the pipeline

```csharp
IResiliencePipeline pipeline = new ResiliencePipelineBuilder()
    .UseRetry(options =>
    {
        options.MaxRetryAttempts = 5;
        options.Delay = TimeSpan.FromMilliseconds(200);
        options.BackoffType = DelayBackoffType.Exponential;
    })
    .Build();
```

## Example 2: Use retry callbacks with a generic pipeline

```csharp
IResiliencePipeline<string> pipeline = new ResiliencePipelineBuilder<string>()
    .UseRetry(options =>
    {
        options.OnRetry = args => ValueTask.CompletedTask;
    })
    .Build();
```
