# Cohesion Coding Rules

This document defines specific coding standards and rules for the Cohesion project. These rules are enforced by AI agents and code reviews.

## General Rules

### ✅ Required Patterns

1. **Always use file-scoped namespaces**
   ```csharp
   namespace Assimalign.Cohesion.Database;
   
   public class DatabaseEngine { }
   ```

2. **Use `CohesionProjectReference` for internal project dependencies**
   ```xml
   <CohesionProjectReference Include="Assimalign.Cohesion.Core" />
   ```

3. **Use `CohesionPackageReference` for NuGet packages**
   ```xml
   <CohesionPackageReference Include="Newtonsoft.Json" />
   ```

4. **Namespace MUST match assembly name exactly**
   - Assembly: `Assimalign.Cohesion.Database.Documents`
   - Namespace: `namespace Assimalign.Cohesion.Database.Documents;`

5. **All libraries MUST target `net10.0`**
   ```xml
   <PropertyGroup>
     <TargetFramework>net10.0</TargetFramework>
   </PropertyGroup>
   ```
   - However this can be generally disregarded as the target framework is managed in `build\Targets\TargetFramework.props`

6. **Preview language features MUST be enabled**
   ```xml
   <PropertyGroup>
     <LangVersion>Preview</LangVersion>
     <EnablePreviewFeatures>true</EnablePreviewFeatures>
   </PropertyGroup>
   ```

7. **Markdown files MUST use uppercase snake casing**
   - ✅ `README.md`, `CONTRIBUTING.md`, `LICENSE`
   - ❌ `readme.md`, `contributing.md`

8. **Prefer direct throws or .NET 10 extension type methods over ThrowHelpers**
   - Use direct `throw` statements or framework guard APIs when the logic is local
   - If reusable throw behavior is needed, implement it as a .NET 10 extension type method in `Extensions/`

### ❌ Forbidden Patterns

1. **NEVER use block-scoped namespaces**
   ```csharp
   // ❌ WRONG
   namespace Assimalign.Cohesion.Database
   {
       public class DatabaseEngine { }
   }
   ```

2. **NEVER use relative paths in project references**
   ```xml
   <!-- ❌ WRONG -->
   <ProjectReference Include="..\..\Core\Assimalign.Cohesion.Core\src\Assimalign.Cohesion.Core.csproj" />
   ```

3. **NEVER add package references without adding to centralized versions**
   - First add to `build/Targets/PackageReferences.targets`
   - Then use `CohesionPackageReference`

4. **NEVER use `PackageReference` directly**
   ```xml
   <!-- ❌ WRONG -->
   <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
   ```
5. **NEVER use any of the following packages**
   - Any `Microsoft.Extensions.*`

6. **NEVER create public classes without XML documentation**
   ```csharp
   // ❌ WRONG - Missing documentation
   public interface IDatabase { }
   
   // ✅ CORRECT
   /// <summary>
   /// Provides database access functionality.
   /// </summary>
   public interface IDatabase { }
   ```

7. **NEVER introduce `ThrowHelper` or `ThrowHelpers` types**
   - Do not add helper classes whose primary purpose is throwing exceptions
   - When touching existing usages, migrate them toward direct throws or .NET 10 extension type methods

## Naming Conventions

### Types

**Interfaces:** Prefix with `I`
```csharp
public interface IDatabase { }
public interface IConfigurationProvider { }
```

**Classes:** PascalCase, descriptive nouns
```csharp
public class DatabaseEngine { }
public class ConfigurationBuilder { }
```

**Exceptions:** Suffix with `Exception`
```csharp
public class DatabaseConnectionException : CohesionException { }
```

**Extension Classes:** Suffix with `Extensions`
```csharp
public static class ServiceCollectionExtensions { }
```

### Members

**Methods:** PascalCase, start with verb
```csharp
public void ExecuteQuery() { }
public async Task<T> GetAsync<T>() { }
```

**Properties:** PascalCase, descriptive nouns
```csharp
public string ConnectionString { get; set; }
public int MaxRetries { get; init; }
```

**Fields:** camelCase with `_` prefix for private fields
```csharp
private readonly string _connectionString;
private int _retryCount;
```

**Constants:** PascalCase for public, camelCase for private
```csharp
public const int DefaultTimeout = 30;
private const int maxRetries = 3;
```

**Parameters:** camelCase
```csharp
public void Execute(string connectionString, int timeout) { }
```

**Local Variables:** camelCase
```csharp
var connectionString = "...";
int retryCount = 0;
```

## Code Organization

### Folder Structure Rules

Libraries MUST follow this structure:
```
libraries/{Category}/Assimalign.Cohesion.{Library}/
├── src/
│   ├── Abstractions/      # Interfaces only
│   ├── Extensions/        # Extension methods
│   ├── Internal/          # Internal implementation
│   ├── Exceptions/        # Custom exceptions
│   ├── ValueObjects/      # Value types
│   └── [Feature folders]
└── tests/
    ├── TestObjects/       # Test fixtures
    └── Shared/            # Shared test code
```

### File Organization Rules

1. **One public type per file** (exceptions: nested types, related enums)
2. **File name MUST match primary type name**
   - `DatabaseEngine.cs` contains `class DatabaseEngine`
3. **Extension methods** in partial classes in `Extensions/` folder
4. **Test files** named `{Feature}Tests.cs`

### Using Directives

**Order:**
1. System namespaces
2. Third-party namespaces
3. Cohesion namespaces
4. Blank line before code

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Configuration;

namespace Assimalign.Cohesion.Database;
```

**Never use global usings or project-level `<Using Include="...">` items. Add explicit `using` directives in each file instead.**

## Access Modifiers

### Default Visibility Rules

1. **Implementation classes:** `internal` by default
   ```csharp
   internal class DatabaseConnectionPool { }
   ```

2. **Public APIs:** Use interfaces
   ```csharp
   public interface IDatabase { }
   internal class Database : IDatabase { }
   ```

3. **Extension methods:** Always `public static`
   ```csharp
   public static class DatabaseExtensions
   {
       public static IServiceCollection AddDatabase(this IServiceCollection services)
       {
           services.AddSingleton<IDatabase, Database>();
           return services;
       }
   }
   ```

4. **Nested types:** Match outer type visibility unless explicitly different

## Documentation Standards

### XML Documentation Requirements

**Public APIs MUST have:**
- `<summary>` - Brief description
- `<param>` - For each parameter
- `<returns>` - For non-void methods
- `<exception>` - For thrown exceptions
- `<remarks>` - For additional details (optional)

**Example:**
```csharp
/// <summary>
/// Executes a database query asynchronously.
/// </summary>
/// <param name="query">The SQL query to execute.</param>
/// <param name="parameters">Query parameters to bind.</param>
/// <param name="cancellationToken">Cancellation token for the operation.</param>
/// <returns>A task representing the query result.</returns>
/// <exception cref="DatabaseConnectionException">Thrown when connection fails.</exception>
/// <remarks>
/// This method automatically retries on transient failures up to 3 times.
/// </remarks>
public async Task<QueryResult> ExecuteAsync(
    string query,
    Dictionary<string, object> parameters,
    CancellationToken cancellationToken = default)
{
    // Implementation
}
```

**Internal types MAY omit XML docs** but should use code comments for complex logic

## Testing Standards

### Test Naming

**Test class:** `{Feature}Tests`
```csharp
public class DatabaseConnectionTests { }
```

**Test methods:** `{Method}_{Scenario}_{ExpectedBehavior}`
```csharp
[Fact]
[DisplayName("Cohesion Test [Database] - Execute: Should retry on transient failure")]
public async Task Execute_OnTransientFailure_ShouldRetry()
{
    // Test implementation
}
```

### Test Structure

**Use AAA pattern:**
```csharp
[Fact]
public void Cache_OnMiss_ShouldReturnNull()
{
    // Arrange
    var cache = new MemoryCache();
    
    // Act
    var result = cache.Get("nonexistent");
    
    // Assert
    result.Should().BeNull();
}
```

### Test Assertions

**Prefer Shouldly or FluentAssertions:**
```csharp
// ✅ Shouldly
result.ShouldNotBeNull();
result.Count.ShouldBe(5);
result.ShouldContain(x => x.Id == "123");

// ✅ FluentAssertions
result.Should().NotBeNull();
result.Count.Should().Be(5);
result.Should().Contain(x => x.Id == "123");

// ❌ Traditional Assert (avoid)
Assert.NotNull(result);
Assert.Equal(5, result.Count);
```

## Pattern Requirements

### Interface-First Design

**Always define interfaces for public APIs:**
```csharp
// 1. Define interface
public interface ICache
{
    Task<T?> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value);
}

// 2. Implement internally
internal class MemoryCache : ICache
{
    public Task<T?> GetAsync<T>(string key) { /* ... */ }
    public Task SetAsync<T>(string key, T value) { /* ... */ }
}

// 3. Register via extension method
public static class CacheExtensions
{
    public static IServiceCollection AddMemoryCache(this IServiceCollection services)
    {
        return services.AddSingleton<ICache, MemoryCache>();
    }
}
```

### Async/Await Rules

1. **Async methods MUST have `Async` suffix**
   ```csharp
   public async Task<string> GetDataAsync() { }
   ```

2. **Always accept `CancellationToken` for async operations**
   ```csharp
   public async Task<T> GetAsync(string key, CancellationToken cancellationToken = default)
   {
       // Implementation
   }
   ```

3. **Avoid `async void`** except for event handlers
   ```csharp
   // ❌ WRONG
   public async void Process() { }
   
   // ✅ CORRECT
   public async Task ProcessAsync() { }
   ```

### Exception Handling

1. **Catch specific exceptions, not `Exception`**
   ```csharp
   try
   {
       await database.ConnectAsync();
   }
   catch (DatabaseConnectionException ex)
   {
       // Handle connection failure
   }
   ```

2. **Use custom exceptions for domain errors**
   ```csharp
   public class InvalidConfigurationException : CohesionException
   {
       public InvalidConfigurationException(string key) 
           : base($"Configuration key '{key}' is invalid or missing.") { }
   }
   ```

3. **Preserve stack trace when rethrowing**
   ```csharp
   // ✅ CORRECT
   catch (Exception ex)
   {
       logger.LogError(ex, "Operation failed");
       throw;
   }
   
   // ❌ WRONG - Loses stack trace
   catch (Exception ex)
   {
       throw ex;
   }
   ```

4. **Avoid `ThrowHelper` patterns**
   - Prefer direct `throw` statements for local guard clauses
   - If throwing logic must be reused, use a .NET 10 extension type method instead of a helper class

## Performance Guidelines

### Memory Allocation

1. **Prefer `ValueTask<T>` for frequently called async methods**
   ```csharp
   public ValueTask<T?> GetFromCacheAsync(string key)
   {
       if (cache.TryGetValue(key, out var value))
           return new ValueTask<T?>(value);
       
       return LoadFromDatabaseAsync(key);
   }
   ```

2. **Use `Span<T>` and `Memory<T>` for buffer operations**

3. **Avoid allocations in hot paths**

### AOT Compatibility

All libraries MUST be AOT-compatible:

```xml
<PropertyGroup>
  <IsAotCompatible>true</IsAotCompatible>
</PropertyGroup>
```

**Avoid:**
- Reflection-based serialization
- Dynamic code generation at runtime
- `Assembly.LoadFrom()`
- Runtime type inspection without source generators

## Version Control

### Commit Messages

Follow conventional commits format:

```
type(scope): subject

body

footer
```

**Types:**
- `feat`: New feature
- `fix`: Bug fix
- `docs`: Documentation changes
- `refactor`: Code refactoring
- `test`: Test additions/changes
- `chore`: Build/tooling changes

**Examples:**
```
feat(database): add connection pooling support
fix(cache): resolve memory leak in expiration logic
docs(readme): update build instructions
refactor(config): simplify provider registration
test(hosting): add lifecycle event tests
chore(build): update to .NET 10.0.101
```

### Branch Naming

- `main` - Production-ready code
- `development` - Integration branch
- `feature/{name}` - New features
- `fix/{name}` - Bug fixes
- `docs/{name}` - Documentation updates

## Code Review Checklist

Before submitting code, ensure:

- [ ] File-scoped namespaces used
- [ ] `CohesionProjectReference` used for internal dependencies
- [ ] `CohesionPackageReference` used for packages
- [ ] XML documentation on all public APIs
- [ ] Tests added/updated
- [ ] Exception types inherit from `CohesionException`
- [ ] No new `ThrowHelper` or `ThrowHelpers` types introduced
- [ ] Async methods have `Async` suffix
- [ ] `CancellationToken` parameter included in async methods
- [ ] No `async void` methods (except event handlers)
- [ ] No hardcoded package versions in project files
- [ ] Markdown files use uppercase names
- [ ] Code follows existing patterns in the category

---

**For questions about these rules, see `.github/copilot-instructions.md` for detailed guidance.**
