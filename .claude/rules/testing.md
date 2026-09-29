---
paths:
  - "**/tests/**"
---

# Testing

## Test naming

**Test class:** `{Feature}Tests`
```csharp
public class DatabaseConnectionTests { }
```

**Test method:** `{Method}_{Scenario}_{ExpectedBehavior}`, with the display name set through the `Fact`/`Theory` attribute
```csharp
[Fact(DisplayName = "Cohesion Test [Database] - Execute: Should retry on transient failure")]
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

## Running tests — pass the project, not the folder

`dotnet test <dir>` works only while a `tests/` folder holds exactly one project. Where it holds
two — a test project beside a `.TestHost` or fixture project — the command fails with
`MSB1050: Specify which project or solution file to use`, and **a scripted loop that filters
output cannot tell that failure apart from a project with no tests**, so the suite silently never
runs. Verification loops must pass the csproj path:

```bash
dotnet test <project>/tests/<project>.Tests.csproj
```

This is not hypothetical: it hid `Assimalign.Cohesion.Database.Testing` — the one suite that
builds a fixture through the real SDK and runs the generated apphost end to end — from twenty
consecutive verification passes on a single branch.

**A suite with a setup prerequisite states it in the project's `README.md`.** If tests need a
packed local feed, a generated build task, or anything else a plain `dotnet test` does not do, say
so where someone editing that project will read it, and name the existing repo command that does
it (`installer/scripts/Install-Local.ps1` for the local package feed). Do not write a new bootstrap
script for what the dev loop already does.

## Coverage expectations

- Add or update tests for behavior changes.
- Spec-driven services (protocol implementations, RFC-backed features) need unit tests **plus** compliance or interoperability tests.
- Preserve NativeAOT and trimming compatibility in code and tests.

## Cancellation in async tests

Async test methods should still exercise `CancellationToken` where the system under test takes one. The repo currently pins xUnit v2 (no ambient `TestContext`), so create a token in the test — e.g. a `CancellationTokenSource` with a timeout — or pass `CancellationToken.None` when cancellation is not the behavior under test.
