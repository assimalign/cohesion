# Assimalign.Cohesion.Caching Design

## Design Intent

The library is intentionally narrow. It centers on contract-first caching primitives so other libraries can depend on an in-process cache abstraction without choosing a storage strategy too early.

## Implementation Note

Examples below document the intended public shape; some members still throw NotImplementedException today.

## Architecture

- ICache is the minimal core contract and keeps the public surface easy to embed.
- IMemoryCache, IDistributedCache, and ICacheEntry give the package room to grow into richer caching scenarios.
- The current MemoryCache implementation is still incomplete, so the package is stronger as an API contract than as a finished runtime component today.

## Layout Example

```text
Assimalign.Cohesion.Caching/
  src/
    Assimalign.Cohesion.Caching.csproj
    Abstractions/
    Extensions/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Basic cache interaction

```csharp
ICache cache = new MemoryCache();

cache.Add("user:42", userProfile);

if (cache.TryGetValue("user:42", out object value))
{
    Console.WriteLine(value);
}
```

## Example 2: Get-or-add flow

```csharp
ICache cache = new MemoryCache();

object profile = cache.GetOrAdd("user:42", _ => LoadUserProfile());
cache.Remove("user:42");
cache.Clear();
```
