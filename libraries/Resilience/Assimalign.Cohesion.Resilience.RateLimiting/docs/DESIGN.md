# Assimalign.Cohesion.Resilience.RateLimiting Design

## Design Intent

This project is earlier than the retry and timeout packages. The current code reads like a spike around System.Threading.RateLimiting, which is useful for documenting intent even though the package is not finished.

## Implementation Note

Examples below emphasize the intended extension point more than the current implementation depth.

## Architecture

- The long-term role is to adapt .NET rate-limiting primitives into a resilience strategy package.
- A finished version should attach through the same builder-extension model as the other strategy packages.
- Right now the package is exploratory rather than production-ready.

## Layout Example

```text
Assimalign.Cohesion.Resilience.RateLimiting/
  src/
    Assimalign.Cohesion.Resilience.RateLimiting.csproj
    Internal/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Current exploratory direction

```csharp
var limiter = new System.Threading.RateLimiting.ConcurrencyLimiter(
    new System.Threading.RateLimiting.ConcurrencyLimiterOptions
    {
        PermitLimit = 4,
        QueueLimit = 16
    });

using var lease = await limiter.AcquireAsync();
```

## Example 2: Target pipeline experience

```csharp
var pipeline = new ResiliencePipelineBuilder()
    // .UseRateLimiter(options => { ... })
    .Build();
```
