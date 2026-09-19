# Assimalign.Cohesion.FileSystem — Design

## Design intent

A single `IFileSystem` contract that every concrete backend implements
identically. Callers depend on the contract, not the backend, so a service
can swap in-memory storage for a tested fixture, isolated storage for
per-user persistence, and physical storage in production — without changing
its consumer surface.

The contract is intentionally narrow:

- File / directory / info hierarchy
- Read-write streams for sequential consumers
- Random-access handles with synchronous and asynchronous positional I/O and explicit durability
- Change-token-shaped watch events
- Enumeration with optional recursion
- Lifetime via `IDisposable` + `IAsyncDisposable`

## Positional I/O and durability

`IFileSystemFile.OpenHandle(FileMode, FileAccess, FileShare)` returns a caller-owned
`IFileSystemFileHandle`. A storage engine reads and writes pages at explicit byte offsets;
`Stream` has a shared cursor and does not expose a durable-flush contract. The handle supplies
`Read` / `ReadAsync`, `Write` / `WriteAsync`, current `Length`, `SetLength`, and
`Flush(bool)` / `FlushAsync(bool)`. Offset-addressed operations can be issued concurrently
without changing another operation's position. Reads may return fewer bytes at end of file,
and writes beyond the end extend the file. The existing `Open` overloads remain unchanged for
Configuration and Web.StaticFiles consumers.

`SupportsDurableFlush` describes the actual backing file, not an optimistic default. When it
is `true`, `Flush(durable: true)` must reach durable storage before returning. When it is
`false`, both synchronous and asynchronous durable flush throw `NotSupportedException`.
A storage engine that asks for durability and silently does not get it is worse than one
that cannot start; non-durable providers must fail explicitly, never silently degrade.
`Flush(durable: false)` only flushes buffers and makes no persistence guarantee.

| Provider | `SupportsDurableFlush` | Mechanism |
|----------|:----------------------:|-----------|
| Physical | `true` | `File.OpenHandle` and `RandomAccess`; durable flush uses `RandomAccess.FlushToDisk`. |
| InMemory | `false` | Offset I/O over the existing buffer; durable flush throws. |
| IsolatedStorage | `true` | The store's `IsolatedStorageFileStream.Flush(true)` forwards to its backing `FileStream.Flush(true)`. The store does not expose a usable `SafeFileHandle`, so each handle serializes seek and I/O internally. |
| Aggregate | Underlying file's value | Returns the resolved provider's handle directly, including its durability behavior. |

Dispose the handle with `using` or `await using` to release its resources and sharing
registration. Disposal is idempotent, and operations on a disposed handle throw
`ObjectDisposedException`. Async methods accept cancellation tokens; cancellation prevents
an operation from starting or waiting further, but cannot undo already completed I/O.
The interface and providers retain `net10.0`, NativeAOT compatibility, and no reflection or
`Microsoft.Extensions.*` dependencies. Routing database storage through this handle belongs
to a separate implementation step.

## Error model

Every provider raises `FileSystemException` with one of the explicit codes
in `FileSystemErrorCode`. The base class has `[DoesNotReturn]` static helpers
(`ThrowFileNotFound`, `ThrowReadOnly`, etc.) so providers don't construct
exceptions inline.

Handle I/O also exposes the BCL argument, access, cancellation, and disposal exceptions.
In particular, unsupported durable flush raises `NotSupportedException` as required by the
handle contract; it is not wrapped as a generic file-system error.

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
