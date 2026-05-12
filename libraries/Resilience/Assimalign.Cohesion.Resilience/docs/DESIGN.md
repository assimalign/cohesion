# Assimalign.Cohesion.Resilience Design

## Design Intent

The base package focuses on composition rather than policy. It defines how strategies wrap callbacks, how outcomes are represented, and how the pipeline is built so focused sibling packages can contribute concrete behaviors.

## Architecture

- ResiliencePipelineBuilder composes strategies from the outside in and produces executable pipelines.
- Outcome and IResilienceContext give every strategy a shared execution contract.
- Concrete policies such as retry and timeout are intentionally pushed into sibling packages that extend the base builder.

## Layout Example

```text
Assimalign.Cohesion.Resilience/
  src/
    Assimalign.Cohesion.Resilience.csproj
    _old/
    Abstractions/
    Delegates/
    Exceptions/
    Extensions/
    Internal/
    Properties/
    ValueTypes/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Add a custom strategy to the pipeline

```csharp
IResiliencePipeline pipeline = new ResiliencePipelineBuilder()
    .UseStrategy(async (callback, context, state) =>
    {
        try
        {
            await callback(context, state);
            return Outcome.Success;
        }
        catch (Exception exception)
        {
            return Outcome.Failure(exception);
        }
    })
    .Build();
```

## Example 2: Compose the base pipeline with strategy packages

```csharp
IResiliencePipeline pipeline = new ResiliencePipelineBuilder()
    .UseRetry(options =>
    {
        options.MaxRetryAttempts = 3;
    })
    .UseTimeout(options =>
    {
        options.Timeout = TimeSpan.FromSeconds(2);
    })
    .Build();
```
