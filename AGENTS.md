# Cohesion Coding Rules

This file is the canonical instruction source for AI agents working in this repository. The GitHub Copilot companion file at `.github/copilot-instructions.md` should mirror this document rather than introduce competing rules.

This document defines specific coding standards and rules for the Cohesion project. These rules are enforced by AI agents and code reviews.

## Repository Context

### Development Environment

- .NET SDK `10.0.101` or later is required and pinned in `global.json`
- Projects compile with `LangVersion=Preview` and `EnablePreviewFeatures=true`
- Libraries target `net10.0` through the shared build configuration
- NativeAOT compatibility is a standing requirement across the repo

### Quick Commands

```powershell
# Build the repository
dotnet build

# Build a specific project
dotnet build libraries/Core/Assimalign.Cohesion.Core/src/Assimalign.Cohesion.Core.csproj

# Run a project's tests
dotnet test libraries/Core/Assimalign.Cohesion.Core/tests/

# Create packages
dotnet pack --configuration Release

# Clean outputs
dotnet clean
```

### Output Directories

- `_out/packages/` for packaged library outputs
- `_out/dotnet/sdk/` for SDK and build output
- `_out/code-generation/` for generated code artifacts

### Repository Structure

- `libraries/` contains shared libraries, infrastructure, runtime, and cross-service foundations
- `resources/` contains service and resource implementations
- `build/` contains custom MSBuild logic, centralized targets, and package-version management
- `sdks/` contains Cohesion SDK projects
- `extensions/` and `tooling/` contain developer tooling and integration surfaces
- `docs/` contains repository-level documentation

### Build System Context

- Prefer Cohesion-specific MSBuild items over stock items where available
- Internal dependencies should use `CohesionProjectReference`
- External packages should use `CohesionPackageReference`
- Central package versions are managed in `build/Targets/PackageReferences.targets`
- Strongly typed value objects may be generated through `CohesionCodeGenValueType`

### GitHub Project Execution Metadata

When work is coming from the Cohesion GitHub Project, treat project fields as execution guidance rather than as decorative labels.

- `Priority` expresses urgency and criticality. Lower numbers are higher priority, so `P001` should be considered before `P002`.
- `Wave` expresses planned delivery order. Lower numbers are earlier waves, so `W01` should generally be delivered before `W02` and `W03`.
- When selecting work autonomously, prefer items that are both unblocked and in the earliest available `Priority` and `Wave`.
- Do not pull later-wave work forward ahead of earlier-wave blockers unless the user explicitly asks for it or the dependency graph makes prerequisite work necessary.
- If issue body details, dependency relationships, `Priority`, and `Wave` conflict, resolve them in this order: explicit user instruction, dependency or blocker relationships, `Priority`, then `Wave`.
- Preserve later-wave requirements in planning and design notes even when only implementing current-wave scope.
- When a ticket requires prerequisite work from another ticket, call that out explicitly rather than silently skipping the project ordering.

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
   - Exception: API reference files under `docs/Assembly/` may mirror namespace and type names directly, for example `docs/Assembly/System.IO/Glob.md`

8. **Prefer direct throws or .NET 10 extension type methods over ThrowHelpers**
   - Use direct `throw` statements or framework guard APIs when the logic is local
   - If reusable throw behavior is needed, implement it as a .NET 10 extension type method in `Extensions/`

9. **Use the .NET 10 `extension(...)` syntax for extension members**
   - Define new extension members with `extension(...)` blocks
   - Do not use the legacy `this` parameter syntax for new extension members

10. **Scope exception roots to a library or service family**
   - Prefer local roots such as `FileSystemException`, `HttpException`, or `DatabaseException` when a service area needs a shared exception base
   - Area-root exceptions should inherit directly from `Exception` or `SystemException` unless there is a strong BCL reason to do otherwise
   - Keep exception inheritance local to the owning area instead of introducing framework-wide base exception dependencies

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

8. **NEVER declare new extension members with the legacy `this` parameter syntax**
   ```csharp
   // ❌ WRONG
   public static class DatabaseExtensions
   {
       public static IServiceCollection AddDatabase(this IServiceCollection services)
       {
           return services;
       }
   }
   ```

9. **NEVER introduce new framework-wide base exception types for unrelated areas**
   - Do not create or revive cross-framework roots such as `CohesionException` or `NetworkException`
   - Unrelated libraries should not depend on a shared exception ancestry just to satisfy framework conventions

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
│   ├── Extensions/        # Extension members
│   ├── Internal/          # Internal implementation
│   ├── Exceptions/        # Custom exceptions
│   ├── ValueObjects/      # Value types
│   └── [Feature folders]
├── docs/
│   ├── OVERVIEW.md        # Project overview
│   ├── DESIGN.md          # Project design notes
│   └── Assembly/          # API reference by namespace and type
└── tests/
    ├── TestObjects/       # Test fixtures
    └── Shared/            # Shared test code
```

### File Organization Rules

1. **One public type per file** (exceptions: nested types, related enums)
2. **File name MUST match primary type name**
   - `DatabaseEngine.cs` contains `class DatabaseEngine`
   - Exception: when several files represent variants of the same root abstraction, prefer grouped root-first filenames so related files sort together
   - Example: use `Http2Frame.Header.cs` and `Http2Frame.Ping.cs` instead of `HeaderHttp2Frame.cs` and `PingHttp2Frame.cs`
   - This grouped naming should be used for implementation families that share the same abstraction root even if the concrete type name remains variant-first
3. **Extension members** in partial classes in `Extensions/` folder using `extension(...)`
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

3. **Extension containers:** Always `public static`, with members declared inside `extension(...)`
   ```csharp
   public static class DatabaseExtensions
   {
       extension(IServiceCollection services)
       {
           public IServiceCollection AddDatabase()
           {
               services.AddSingleton<IDatabase, Database>();
               return services;
           }
       }
   }
   ```

4. **Nested types:** Match outer type visibility unless explicitly different

## Documentation Standards

### Project Level Documentation Requirements

Every project with `src/` and `tests/` should also have a sibling `docs/` folder.

Required project-level documentation:
- `docs/OVERVIEW.md` describing the project purpose, scope, dependencies, and usage at a high level
- `docs/DESIGN.md` describing architecture, important design choices, lifecycle behavior, extension points, operational concerns, and known constraints
- `docs/Assembly/` containing API reference material organized by namespace and type

Assembly documentation layout:
- Namespace folders under `docs/Assembly/` should mirror the documented namespace, for example `docs/Assembly/System.IO/`
- Type documentation files inside those folders should mirror the type name, for example `docs/Assembly/System.IO/Glob.md`
- Assembly API docs are the exception to the uppercase-markdown naming rule because they intentionally mirror CLR namespace and type names
- API reference docs should outline the public surface area, constructor or factory behavior, methods, properties, exceptions, and usage notes for the documented type

### Area Level Documentation Requirements

Each major area root should contain a `README.md` that provides an overview of that area.

Examples:
- `resources/Web/README.md`
- `resources/Database/README.md`
- `libraries/Core/README.md`

Area-level `README.md` files should summarize:
- the purpose of the area
- the major projects or services it contains
- how the area fits into the L1, L2, and L3 layering model
- important dependencies on other areas
- links to project-level `OVERVIEW.md` and `DESIGN.md` files where relevant

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

// 3. Register via extension member
public static class CacheExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddMemoryCache()
        {
            return services.AddSingleton<ICache, MemoryCache>();
        }
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
   public class InvalidConfigurationException : Exception
   {
       public InvalidConfigurationException(string key) 
           : base($"Configuration key '{key}' is invalid or missing.") { }
   }
   ```
   - When multiple implementations within the same area need a shared root, define an area-specific base such as `FileSystemException`
   - Avoid cross-framework exception roots that force unrelated libraries to share the same ancestry

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
- [ ] Exception roots stay scoped to the owning library or service area
- [ ] No new `ThrowHelper` or `ThrowHelpers` types introduced
- [ ] New extension members use `extension(...)` instead of the legacy `this` parameter syntax
- [ ] Async methods have `Async` suffix
- [ ] `CancellationToken` parameter included in async methods
- [ ] No `async void` methods (except event handlers)
- [ ] No hardcoded package versions in project files
- [ ] Markdown files use uppercase names
- [ ] Code follows existing patterns in the category

---

**Canonical source:** `AGENTS.md`

**Copilot mirror:** `.github/copilot-instructions.md` should stay aligned with this file and should not override it.
