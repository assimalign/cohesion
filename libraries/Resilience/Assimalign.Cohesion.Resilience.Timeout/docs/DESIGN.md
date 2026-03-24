# Assimalign.Cohesion.Resilience.Timeout Design

## Design Intent

The timeout package follows the same narrow strategy-package model as retry: keep the core pipeline generic, and put timeout-specific configuration and execution behavior in a separate assembly.

## Architecture

- TimeoutStrategyOptions owns fixed and dynamic timeout selection.
- TimeoutResilienceExtensions attaches the strategy to pipeline builders.
- TimeoutRejectedException keeps timeout failures explicit at the strategy boundary.

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
