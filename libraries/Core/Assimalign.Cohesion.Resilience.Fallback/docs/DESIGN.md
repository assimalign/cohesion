# Assimalign.Cohesion.Resilience.Fallback Design

## Design Intent

This package is intended to contribute fallback behavior without expanding the core resilience package. The implementation is still only a placeholder, but the extension-point story is already visible.

## Implementation Note

Examples below emphasize the intended extension point more than the current implementation depth.

## Architecture

- A finished version should plug into ResiliencePipelineBuilder through focused extensions.
- Fallback result selection and exception handling should stay local to this package.
- The current public surface is placeholder-only.

## Layout Example

```text
Assimalign.Cohesion.Resilience.Fallback/
  src/
    Assimalign.Cohesion.Resilience.Fallback.csproj
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Target builder experience

```csharp
var pipeline = new ResiliencePipelineBuilder()
    // .UseFallback(options => { ... })
    .Build();
```

## Example 2: Suggested package shape

```text
src/
  Extensions/
  Internal/
  Options/
  Exceptions/

Each strategy package should stay focused on one concern: fallback handling.
```
