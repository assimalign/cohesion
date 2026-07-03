# Testing

## Test naming

**Test class:** `{Feature}Tests`
```csharp
public class DatabaseConnectionTests { }
```

**Test method:** `{Method}_{Scenario}_{ExpectedBehavior}`
```csharp
[Fact]
[DisplayName("Cohesion Test [Database] - Execute: Should retry on transient failure")]
public async Task Execute_OnTransientFailure_ShouldRetry()
{
    // Test implementation
}
```

## Test structure — AAA pattern

```csharp
[Fact]
public void Cache_OnMiss_ShouldReturnNull()
{
    // Arrange
    var cache = new MemoryCache();
    
    // Act
    var result = cache.Get("nonexistent");
    
    // Assert
    result.ShouldBeNull();
}
```

## Assertions — Shouldly

Shouldly is the single assertion library for the repo.

```csharp
// ✅ Shouldly
result.ShouldNotBeNull();
result.Count.ShouldBe(5);
result.ShouldContain(x => x.Id == "123");

// ❌ FluentAssertions — forbidden (v8+ moved to a paid commercial license; migrated out 2026-07)
result.Should().NotBeNull();

// ❌ Avoid traditional Assert
Assert.NotNull(result);
Assert.Equal(5, result.Count);
```

Caveat: Shouldly's string `ShouldContain`/`ShouldNotContain` default to case-INSENSITIVE comparison. Pass `Case.Sensitive` when asserting exact fragments (e.g., serialized JSON).

## Test folder layout

```
libraries/{Category}/Assimalign.Cohesion.{Library}/tests/
├── TestObjects/    # Test fixtures and supporting types
├── Shared/         # Shared test code across multiple test files
└── {Feature}Tests.cs
```

## Cancellation in async tests

Async test methods should still respect `CancellationToken` where the system under test takes one. Pass `TestContext.Current.CancellationToken` or equivalent when available.
