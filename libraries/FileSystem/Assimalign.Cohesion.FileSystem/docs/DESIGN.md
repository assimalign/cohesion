# Assimalign.Cohesion.FileSystem — Design

## Design intent

A single `IFileSystem` contract that every concrete backend implements
identically. Callers depend on the contract, not the backend, so a service
can swap in-memory storage for a tested fixture, isolated storage for
per-user persistence, and physical storage in production — without changing
its consumer surface.

The contract is intentionally narrow:

- File / directory / info hierarchy
- Read-write streams (no async file I/O surface — `Stream` already gives that)
- Change-token-shaped watch events
- Enumeration with optional recursion
- Lifetime via `IDisposable` + `IAsyncDisposable`

## Error model

Every provider raises `FileSystemException` with one of the explicit codes
in `FileSystemErrorCode`. The base class has `[DoesNotReturn]` static helpers
(`ThrowFileNotFound`, `ThrowReadOnly`, etc.) so providers don't construct
exceptions inline.

| Code | When |
|------|------|
| `NotFound` | The requested path doesn't exist. |
| `Conflict` | The destination path already exists when it shouldn't. |
| `ReadOnly` | A mutating op was rejected because the file system is read-only. |
| `AccessDenied` | OS / store rejected the operation (permissions, locked file). |
| `NotEnoughSpace` | Write would exceed the configured quota. |
| `PathTooLong` | The OS reported `PathTooLongException`. |
| `PathInUse` | Another handle holds an incompatible share. |
| `Other` | Catch-all; should be paired with a wrapped inner exception. |

## Factory and lifecycle

`FileSystemFactoryBuilder` is single-use. After `Build()` returns the
factory, further calls throw `InvalidOperationException` — preserving
ownership clarity. The factory itself caches each created file system by
name (case-insensitive lookup) and cascades `Dispose` to every materialized
instance.

```csharp
using var factory = new FileSystemFactoryBuilder()
    .AddInMemoryFileSystem(o => o.Name = "scratch")
    .Build();

IFileSystem fs = factory.Create("scratch");       // first call materializes
IFileSystem same = factory.Create("scratch");     // subsequent calls return the cache
Assert.Same(fs, same);
```

## Contract suite

`tests/Shared/FileSystemStandardTests.cs` defines 32 provider-agnostic
contract tests. Each concrete provider inherits the suite via
`<Compile Include="..\..\Assimalign.Cohesion.FileSystem\tests\Shared\FileSystemStandardTests.cs" Link="Shared\FileSystemStandardTests.cs" />`
in its test csproj.

The suite is the source of truth for provider compatibility. A new provider
adds an inheritor, overrides `GetFileSystem()`, and the 32 tests light up
against the new implementation. The Aggregate PR and the IsolatedStorage
work both exercised this pattern — the suite caught nine real bugs in
Physical, an event-path doubling bug in InMemory, and a fan-in glob default
bug in Aggregate before any consumer code shipped.

## Path model

`FileSystemPath` (defined in `Assimalign.Cohesion.Core`, namespace
`System.IO`) is the single representation:

- Uses `/` as the separator on every OS.
- Optional leading `/` marks an absolute path.
- `Merge` performs `..`-aware joining with normalization.
- `GetSegments`, `GetFileName`, `GetDirectoryName` return strongly-typed parts.

Providers normalize incoming paths into absolute form using `Merge` against
their root before any further work.

## Adding a new provider

1. Create `Assimalign.Cohesion.FileSystem.<Name>/src/` and `tests/` under
   `libraries/FileSystem/`.
2. Implement `IFileSystem` (typically using helper types in `Internal/` for
   the directory / file / info wrappers).
3. Add a `FileSystemFactoryBuilder` extension named `Add<Name>FileSystem`.
4. Create `tests/<Name>FileSystemStandardTests.cs` inheriting
   `FileSystemStandardTests`, plus any provider-specific tests in a
   separate file.
5. Add an entry to `.github/workflows/library-filesystem.yml`'s matrix.
6. List the assembly in `frameworks/Assimalign.Cohesion.App.props` under
   the active `<CohesionFrameworkAssembly>` block.
7. Update `libraries/FileSystem/README.md` and add per-package
   `README.md` + `docs/{OVERVIEW,DESIGN}.md`. Update
   `Assimalign.Cohesion.FileSystem/docs/COMPATIBILITY.md` with the
   new row.
