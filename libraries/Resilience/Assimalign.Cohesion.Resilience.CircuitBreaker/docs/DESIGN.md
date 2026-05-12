# Assimalign.Cohesion.Resilience.CircuitBreaker Design

## Design Intent

The role of this package is clear even though the implementation is not finished: contribute one focused strategy and keep its options, state management, and builder extensions isolated from the base pipeline package.

## Implementation Note

Examples below emphasize the intended extension point more than the current implementation depth.

## Architecture

- A finished version should expose builder extensions that plug into ResiliencePipelineBuilder.
- Circuit-breaker state and thresholds should remain local to this package instead of complicating the core resilience abstractions.
- The current assembly is still only a placeholder.

## Layout Example

```text
Assimalign.Cohesion.Resilience.CircuitBreaker/
  src/
    Assimalign.Cohesion.Resilience.CircuitBreaker.csproj
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Target builder experience

```csharp
var pipeline = new ResiliencePipelineBuilder()
    // .UseCircuitBreaker(options => { ... })
    .Build();
```

## Example 2: Suggested package shape

```text
src/
  Extensions/
  Internal/
  Options/
  Exceptions/

Each strategy package should stay focused on one concern: circuit-breaking.
```
