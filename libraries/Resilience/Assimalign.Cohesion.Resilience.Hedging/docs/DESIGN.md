# Assimalign.Cohesion.Resilience.Hedging Design

## Design Intent

The package is meant to isolate hedging-specific concerns such as parallel attempts and winner selection from the core pipeline package. At the moment it is still a placeholder.

## Implementation Note

Examples below emphasize the intended extension point more than the current implementation depth.

## Architecture

- A finished version should expose one focused builder extension for hedging.
- Concurrency and response selection policies belong here rather than in the base resilience assembly.
- The current project is still only a placeholder shell.

## Layout Example

```text
Assimalign.Cohesion.Resilience.Hedging/
  src/
    Assimalign.Cohesion.Resilience.Hedging.csproj
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Target builder experience

```csharp
var pipeline = new ResiliencePipelineBuilder()
    // .UseHedging(options => { ... })
    .Build();
```

## Example 2: Suggested package shape

```text
src/
  Extensions/
  Internal/
  Options/
  Exceptions/

Each strategy package should stay focused on one concern: hedged execution.
```
