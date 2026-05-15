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

## Assertions — Shouldly or FluentAssertions

```csharp
// ✅ Shouldly
result.ShouldNotBeNull();
result.Count.ShouldBe(5);
result.ShouldContain(x => x.Id == "123");

// ✅ FluentAssertions
result.Should().NotBeNull();
result.Count.Should().Be(5);
result.Should().Contain(x => x.Id == "123");

// ❌ Avoid traditional Assert
Assert.NotNull(result);
Assert.Equal(5, result.Count);
```

Pick one library per project and stay consistent within that project.

## Test folder layout

```
libraries/{Category}/Assimalign.Cohesion.{Library}/tests/
├── TestObjects/    # Test fixtures and supporting types
├── Shared/         # Shared test code across multiple test files
└── {Feature}Tests.cs
```

## Cancellation in async tests

Async test methods should still respect `CancellationToken` where the system under test takes one. Pass `TestContext.Current.CancellationToken` or equivalent when available.
