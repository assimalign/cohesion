# Cohesion - AI Agent Instructions

Cohesion is a comprehensive .NET 10 mono-repository providing cloud-native services and infrastructure abstractions. This document provides essential guidance for AI agents working with the codebase.

## Project Overview

**Technology Stack:**
- .NET SDK: 10.0.101 (net10.0)
- Language: C# with Preview features enabled
- Target Platform: Cross-platform (Windows, Linux, macOS)
- Build System: Custom MSBuild with Cohesion extensions
- Testing: xUnit + Shouldly + FluentAssertions
- Version: 9.0.0 (mono-repo fixed versioning)

**Repository Type:** Mono-repository
**Default Branch:** main
**Current Branch:** development

## Development Environment Setup

### Prerequisites
1. **.NET SDK 10.0.101+** - Pinned in `global.json`
2. **LangVersion: Preview** - Required for all compilation
3. **EnablePreviewFeatures: true** - Required in all projects

### Quick Start Commands

```powershell
# Build entire repository
dotnet build

# Build specific library
dotnet build libraries/Core/Assimalign.Cohesion.Core/src/Assimalign.Cohesion.Core.csproj

# Run tests for a specific library
dotnet test libraries/Core/Assimalign.Cohesion.Core/tests/

# Create NuGet packages (Release only)
dotnet pack --configuration Release

# Clean build outputs
dotnet clean
```

### Output Directories
- **Packages:** `_out/packages/` - NuGet packages for libraries
- **SDK:** `_out/dotnet/sdk/` - MSBuild SDK outputs
- **Code Generation:** `_out/code-generation/` - Generated value types

## Repository Structure

```
cohesion/
├── .github/               # GitHub configuration and workflows
├── analyzers/             # Roslyn analyzers for Cohesion
├── build/                 # Custom MSBuild system
│   ├── Build.props        # Core build properties
│   ├── Build.targets      # Core build targets
│   ├── Targets/           # Modular build targets
│   └── Tasks/             # Custom MSBuild tasks
├── docs/                  # Documentation
├── examples/              # Example applications
├── extensions/            # VS extensions and dotnet CLI tools
├── libraries/             # **PRIMARY SOURCE CODE**
│   ├── Core/              # Foundation libraries
│   ├── Database/          # Data storage abstractions
│   ├── MessageHub/        # Messaging services
│   ├── EventHub/          # Event-driven services
│   ├── IdentityHub/       # Authentication/Authorization
│   ├── IoTHub/            # IoT device management
│   ├── Web/               # Web infrastructure
│   └── [30+ more services...]
├── scripts/               # Development scripts
├── sdks/                  # MSBuild SDK projects
└── tooling/               # CLI and Portal tools
```

## Code Organization

### Library Structure

**Naming Convention:** `Assimalign.Cohesion.{Category}[.{SubCategory}[.{Feature}]]`

**Directory Pattern:**
```
libraries/{Category}/Assimalign.Cohesion.{Library}/
├── src/                          # Source code (generates NuGet package)
│   ├── Abstractions/             # Interfaces (I prefix)
│   ├── Extensions/               # Extension methods
│   ├── Internal/                 # Internal implementations
│   ├── Exceptions/               # Custom exceptions
│   ├── ValueObjects/             # Domain value objects
│   ├── Decorators/               # Decorator patterns
│   └── Utilities/                # Helper classes
├── tests/                        # xUnit test project
│   ├── TestObjects/              # Test fixtures
│   └── Shared/                   # Shared test code
├── docs/                         # Library-specific documentation
└── README.md                     # Library readme
```

### Service Categories

The repository is organized into distinct service domains:

**Core Infrastructure:**
- Core, ApplicationModel, Configuration, DependencyInjection
- Hosting, Logging, FileSystem, Caching, Resilience

**Communication & Messaging:**
- MessageHub, EventHub, EmailHub, NotificationHub, Http, Amqp

**Data & Storage:**
- Database (Blob, Documents, Graph, KeyValuePair, Sql, Cache)
- ConfigurationStore, SecretStore, LogSpace, Content

**Identity & Security:**
- IdentityHub, IdentityModel

**Specialized Services:**
- IoTHub, MediaHub, ApiManager, Scheduler, ML, Synthara

**Infrastructure:**
- LoadBalancer, Dns, VpnGateway, NatGateway, OpenTelemetry

## Build System

### Custom MSBuild Items

Cohesion provides custom MSBuild items to simplify project management. **Always prefer these over standard MSBuild items** when available.

#### 1. CohesionProjectReference
Reference internal projects by name only (no paths required):

```xml
<ItemGroup>
  <CohesionProjectReference Include="Assimalign.Cohesion.Core" />
  <CohesionProjectReference Include="Assimalign.Cohesion.Configuration" />
</ItemGroup>
```

**Do NOT use:**
```xml
<!-- ❌ WRONG - Don't use relative paths -->
<ProjectReference Include="..\..\Core\Assimalign.Cohesion.Core\src\Assimalign.Cohesion.Core.csproj" />
```

#### 2. CohesionPackageReference
Add NuGet packages by name (versions managed centrally):

```xml
<ItemGroup>
  <CohesionPackageReference Include="Newtonsoft.Json" />
  <CohesionPackageReference Include="Serilog" />
</ItemGroup>
```

**Package versions are defined in:** `build/Targets/PackageReferences.targets`

**Adding new packages:** Add version definition to `PackageReferences.targets` first.

#### 3. CohesionCodeGenValueType
Generate strongly-typed value objects:

```xml
<ItemGroup>
  <CohesionCodeGenValueType
      Include="ValueTypes\HostId.cs"
      ObjectType="Ulid"
      ObjectNamespace="Assimalign.Cohesion.Hosting"
      ObjectAccessModifier="public"
      IncludeImplicitOperators="true" />
</ItemGroup>
```

**Supported Types:** `Ulid`, `Guid`, `string`, `short`, `int`, `long`, `decimal`,  `double`


### Build Properties

**Key Cohesion Properties:**
```xml
<PropertyGroup>
  <!-- Version -->
  <CohesionVersion>9.0.0</CohesionVersion>
  
  <!-- Target Framework -->
  <TargetFramework>net10.0</TargetFramework>
  <LangVersion>Preview</LangVersion>
  <EnablePreviewFeatures>true</EnablePreviewFeatures>
  
  <!-- AOT Compatibility -->
  <IsAotCompatible>true</IsAotCompatible>
  
  <!-- Output Paths -->
  <CohesionOutputPath>_out/</CohesionOutputPath>
  <CohesionOutputPathForLibraries>_out/packages</CohesionOutputPathForLibraries>
  <CohesionOutputPathForSdk>_out/dotnet/sdk</CohesionOutputPathForSdk>
</PropertyGroup>
```

### Project Type Detection

Projects are automatically classified based on location:

- **Source Projects:** `libraries/{Service}/{Project}/src/*.csproj`
  - Generate NuGet packages in Release configuration
  - Include LICENSE from repository root
  
- **Test Projects:** `libraries/{Service}/{Project}/tests/*.csproj`
  - Marked with `<IsTestProject>true</IsTestProject>`
  - Do not generate packages
  
- **SDK Projects:** `sdks/{ProjectName}/*.csproj`
  - Copy `.props` and `.targets` to `_out/dotnet/sdk/`
  - `<IncludeBuildOutput>false</IncludeBuildOutput>`

## Coding Conventions

### Namespaces

**Use file-scoped namespaces:**
```csharp
namespace Assimalign.Cohesion.Database;

public class DatabaseEngine { }
```

**Not:**
```csharp
// ❌ WRONG - Don't use block-scoped namespaces
namespace Assimalign.Cohesion.Database
{
    public class DatabaseEngine { }
}
```

**Namespace matches assembly name exactly:**
- Assembly: `Assimalign.Cohesion.Database.Documents`
- Namespace: `namespace Assimalign.Cohesion.Database.Documents;`

### Naming Patterns

**Interfaces:** `I` prefix
```csharp
public interface IDatabase { }
public interface IConfigurationProvider { }
```

**Extension Methods:** Use partial classes in `Extensions/` folder
```csharp
// File: Extensions/ServiceProviderBuilderExtensions.cs
namespace Assimalign.Cohesion.DependencyInjection;

public static partial class ServiceProviderBuilderExtensions
{
    public static IServiceCollection AddSingleton<T>(this IServiceCollection services)
        where T : class
    {
        // Implementation
    }
}
```

**Exceptions:** Inherit from `CohesionException`
```csharp
namespace Assimalign.Cohesion.Database;

public class DatabaseConnectionException : CohesionException
{
    public DatabaseConnectionException(string message) 
        : base(message) { }
}
```

**Value Objects:** Place in `ValueObjects/` folder
```csharp
namespace Assimalign.Cohesion.Hosting;

public readonly struct HostId
{
    private readonly Ulid value;
    
    public HostId(Ulid value) => this.value = value;
    
    public static implicit operator Ulid(HostId id) => id.value;
    public static implicit operator HostId(Ulid value) => new(value);
}
```

### Access Modifiers

**Default to internal for implementation details:**
```csharp
internal class DatabaseConnectionPool { }  // ✅ Good - hide implementation
public interface IDatabase { }             // ✅ Good - public API
```

### Documentation

**XML documentation for public APIs:**
```csharp
/// <summary>
/// Represents a database connection abstraction.
/// </summary>
/// <remarks>
/// This interface provides a unified API for interacting with different database providers.
/// </remarks>
public interface IDatabase
{
    /// <summary>
    /// Executes a query asynchronously.
    /// </summary>
    /// <param name="query">The query to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query results.</returns>
    Task<IQueryResult> ExecuteAsync(string query, CancellationToken cancellationToken = default);
}
```

**Markdown files use UPPERCASE snake casing:**
```
✅ README.md
✅ CONTRIBUTING.md
✅ CODE_OF_CONDUCT.md
```

## Testing Patterns

### Test Project Structure

```
tests/
├── UnitTest1.cs              # Placeholder (replace with real tests)
├── {FeatureName}Tests.cs     # Feature-specific tests
├── TestObjects/              # Test fixtures and helpers
└── Shared/                   # Shared test utilities
```

### xUnit Conventions

**Display names with prefix:**
```csharp
[Fact]
[DisplayName("Cohesion Test [Database] - Connection: Should retry on transient failure")]
public async Task Database_ShouldRetryOnTransientFailure()
{
    // Arrange
    var database = new MockDatabase();
    
    // Act
    var result = await database.ExecuteAsync("SELECT * FROM users");
    
    // Assert
    result.Should().NotBeNull();
}
```

**Assertion libraries:**
- **Shouldly:** Fluent assertions with better error messages
- **FluentAssertions:** Alternative fluent assertion syntax

```csharp
using Shouldly;

[Fact]
public void Cache_ShouldReturnCachedValue()
{
    cache.Get("key").ShouldBe("value");
}
```

```csharp
using FluentAssertions;

[Fact]
public void Cache_ShouldReturnCachedValue()
{
    cache.Get("key").Should().Be("value");
}
```

## Common Development Tasks

### Creating a New Library

1. **Create directory structure:**
   ```
   libraries/{Category}/Assimalign.Cohesion.{Name}/
   ├── src/
   │   └── Assimalign.Cohesion.{Name}.csproj
   └── tests/
       └── Assimalign.Cohesion.{Name}.Tests.csproj
   ```

2. **Configure source project (.csproj):**
   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <ItemGroup>
       <CohesionProjectReference Include="Assimalign.Cohesion.Core" />
     </ItemGroup>
   </Project>
   ```

3. **Configure test project (.csproj):**
   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <IsTestProject>true</IsTestProject>
     </PropertyGroup>
     
     <ItemGroup>
       <CohesionProjectReference Include="Assimalign.Cohesion.{Name}" />
       <CohesionPackageReference Include="xunit" />
       <CohesionPackageReference Include="Shouldly" />
     </ItemGroup>
   </Project>
   ```

4. **Add to solution file** (if applicable)

### Adding a New Package Dependency

1. **Add version to `build/Targets/PackageReferences.targets`:**
   ```xml
   <ItemGroup>
     <_CohesionPackage Update="Newtonsoft.Json" Version="13.0.3" />
   </ItemGroup>
   ```

2. **Use in project:**
   ```xml
   <ItemGroup>
     <CohesionPackageReference Include="Newtonsoft.Json" />
   </ItemGroup>
   ```

### Generating Value Types

1. **Add code generation item to .csproj:**
   ```xml
   <ItemGroup>
     <CohesionCodeGenValueType
         Include="ValueTypes\DatabaseId.cs"
         ObjectType="Guid"
         ObjectNamespace="Assimalign.Cohesion.Database"
         ObjectAccessModifier="public"
         IncludeImplicitOperators="true" />
   </ItemGroup>
   ```

2. **Build project** - code is generated before compilation

3. **Generated file location:** `_out/code-generation/create-value-types/DatabaseId.cs`

### Working Across Multiple Libraries

**When making cross-cutting changes:**

1. Identify affected libraries
2. Update interfaces in abstraction libraries first
3. Update implementations in dependent libraries
4. Update tests to reflect changes
5. Build incrementally to catch breaking changes early

**Check for affected projects:**
```powershell
# Find projects referencing a specific library
Get-ChildItem -Recurse -Filter "*.csproj" | 
  Select-String "Assimalign.Cohesion.Core" | 
  Select-Object -ExpandProperty Path -Unique
```

## Architecture Patterns

### Dependency Layers

**L0 - Foundation:** No dependencies
- `Assimalign.Cohesion.Core`
- `Assimalign.Cohesion.Transports`

**L1 - Infrastructure:** Depends on L0
- `Assimalign.Cohesion.Configuration`
- `Assimalign.Cohesion.DependencyInjection`
- `Assimalign.Cohesion.FileSystem`
- `Assimalign.Cohesion.Logging`

**L2 - Application:** Depends on L0-L1
- `Assimalign.Cohesion.Hosting`
- `Assimalign.Cohesion.ApplicationModel`

**L3 - Services:** Depends on L0-L2
- `Assimalign.Cohesion.Database.*`
- `Assimalign.Cohesion.MessageHub`
- `Assimalign.Cohesion.EventHub`

**L4 - Specialized:** Depends on multiple layers
- `Assimalign.Cohesion.Web.*`
- `Assimalign.Cohesion.IdentityHub`

### Common Patterns

**Interface-First Design:**
```csharp
// 1. Define interface
public interface ICache
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);
}

// 2. Implement interface
internal class MemoryCache : ICache
{
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        // Implementation
    }
}

// 3. Provide extension methods for registration
public static class CacheExtensions
{
    public static IServiceCollection AddMemoryCache(this IServiceCollection services)
    {
        services.AddSingleton<ICache, MemoryCache>();
        return services;
    }
}
```

**Builder Pattern:**
```csharp
public interface IConfigurationBuilder
{
    IConfigurationBuilder Add(IConfigurationSource source);
    IConfiguration Build();
}

public class ConfigurationBuilder : IConfigurationBuilder
{
    private readonly List<IConfigurationSource> sources = new();
    
    public IConfigurationBuilder Add(IConfigurationSource source)
    {
        sources.Add(source);
        return this;
    }
    
    public IConfiguration Build()
    {
        return new ConfigurationRoot(sources);
    }
}
```

## Troubleshooting

### Common Build Issues

**Issue:** "Feature 'X' is not available in C# 10.0"
**Solution:** Ensure `<LangVersion>Preview</LangVersion>` is set

**Issue:** "Project reference not found"
**Solution:** Use `CohesionProjectReference` instead of `ProjectReference`

**Issue:** "Package version conflict"
**Solution:** Check `build/Targets/PackageReferences.targets` for centralized version

**Issue:** "Generated code not found"
**Solution:** Run `dotnet build` - code generation happens before compilation

### Getting Help

1. **Documentation:** Check `docs/` folder for detailed guides
2. **Examples:** Review `examples/` for usage patterns
3. **Build System:** See `docs/build/` for MSBuild documentation
4. **Guidelines:** See `docs/guidelines/` for development standards

## Best Practices for AI Agents

### When Adding Features
1. ✅ Follow existing patterns in the same category
2. ✅ Use `CohesionProjectReference` for internal dependencies
3. ✅ Use `CohesionPackageReference` for external packages
4. ✅ Add XML documentation for public APIs
5. ✅ Create corresponding test projects
6. ✅ Use file-scoped namespaces
7. ✅ Mark implementation classes as `internal`
8. ✅ Prefer interfaces for public contracts

### When Making Changes
1. ✅ Search for similar implementations first
2. ✅ Check for existing abstractions before creating new ones
3. ✅ Update affected tests
4. ✅ Maintain consistent naming patterns
5. ✅ Preserve XML documentation
6. ✅ Consider breaking changes impact
7. ✅ Follow dependency layer constraints
8. ✅ Build incrementally to catch errors early

### When in Doubt
1. ✅ Look for similar code in the same service category
2. ✅ Check Core libraries for base implementations
3. ✅ Review existing patterns before inventing new ones
4. ✅ Prefer composition over inheritance
5. ✅ Keep classes focused and single-purpose

## Quick Reference

### File Locations
- **Build Configuration:** `build/`
- **Source Code:** `libraries/{Category}/{Library}/src/`
- **Tests:** `libraries/{Category}/{Library}/tests/`
- **Documentation:** `docs/`
- **Examples:** `examples/`

### Common Commands
```powershell
# Build everything
dotnet build

# Build specific library
dotnet build libraries/Core/Assimalign.Cohesion.Core/src/

# Run tests
dotnet test

# Create packages
dotnet pack --configuration Release

# Clean outputs
dotnet clean
```

### Key Files
- `global.json` - .NET SDK version pin
- `Directory.Build.props` - Root build properties
- `Directory.Build.targets` - Root build targets
- `build/Targets/Version.props` - Version configuration
- `build/Targets/PackageReferences.targets` - Package versions

---

**Note:** This is a living document. Update as patterns evolve and new conventions emerge.
