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
in `FileSystemErrorCode`. `[DoesNotReturn]` static helpers
(`ThrowFileNotFound`, `ThrowReadOnly`, etc.) keep providers from constructing
exceptions inline. The original helpers are declared on the exception type. New
ones are static extension members in `FileSystemExceptionExtensions`, following
the repository rule against throw-helper types: `ThrowPathOutsideRoot` is the
first. Both are called the same way (`FileSystemException.ThrowPathOutsideRoot(path)`),
so the older helpers can move to the extension container without changing callers.

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
| `PathOutsideRoot` | The path resolves outside the provider's root; raised before the backing store is touched (see *Root containment*). |
| `Other` | Catch-all; should be paired with a wrapped inner exception. |

`PathOutsideRoot` was appended after `ReadOnly`, so every earlier code keeps its numeric value;
`FileSystemExceptionTests` pins the ordinals.

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
- `Parse` admits `..` only at the start of a relative path; an interior `..` is an
  `ArgumentException` at conversion time.
- `Merge` navigates: it joins a relative path onto a base, returns a path that already lies
  under the base on a segment boundary, and applies leading `..` segments, which may climb
  above the base but never above its root (drive, leading `/`, or UNC share).
- `GetSegments`, `GetFileName`, `GetDirectoryName` return strongly-typed parts.

`Merge` is not a containment primitive, and no provider uses it to resolve incoming paths.

## Root containment

A provider rooted somewhere confines every path-taking operation to that root; the table below
says which providers do so today. The rule, in the order it runs:

1. **Resolve.** An empty path is the root. A relative path is taken from the root. A rooted path
   names a location in the provider's namespace and is used as given. `.` and `..` segments are
   resolved, so `../public/index.html` from a root of `/srv/public` is back inside.
2. **Check.** The resolved path must equal the root or lie under it on a segment boundary:
   `/srv/public2/x` does not lie under `/srv/public`. Anything else throws
   `FileSystemException` with `FileSystemErrorCode.PathOutsideRoot`.
3. **Use.** Only the checked path reaches the backing store, rebuilt from the root's own text,
   so nothing outside the root is read, created, changed, or even probed for existence.
   `Exists` throws too; it does not answer `false`, because an answer about a location outside
   the root is still an answer about that location.

Copy and move resolve both ends before either is touched.

| Provider | Containment | How "resolve" works | Comparison |
|----------|-------------|---------------------|------------|
| Physical | Enforced (#1180) | `Path.GetFullPath` — the host's own normalization, so the check runs on exactly the string `System.IO` opens. A rooted path must be fully qualified. | Ordinal ignore-case on Windows and Apple platforms, ordinal elsewhere |
| InMemory | Enforced (#1180) | Lexical `.`/`..` resolution beneath the namespace root; `..` at the namespace root stays there, as `/..` is `/` on every host | Ordinal, ignore-case when `IgnoreCase` is set |
| IsolatedStorage | Not yet | Merges onto `/` and hands the rest to `IsolatedStorageFile`, which does not confine `..` | — |
| Aggregate | Delegates | Routes by mount prefix on segment boundaries; the mounted provider confines its own paths | Ordinal |

The physical provider's link policy and Windows specifics are in its own `docs/DESIGN.md`.

### Why containment lives in each provider, not in `FileSystemPath`

**Decision:** `Merge` stays a navigation helper, its bugs fixed, and each provider owns the
containment check for its own namespace. Recorded for #1180.

- **`Merge` keeps navigating.** It is public API with callers that rely on climbing above the
  base: `IFileSystemDirectory.Exists` and `CreateSubdirectory` (the `FileSystemExtensions`
  members) merge a caller's `../sibling` onto a subdirectory's path, which is legitimate as long
  as the provider then keeps the result inside its root, and Core's own tests pin
  `Merge("C:/users/path1/path2", "../../johndoe")`. Making `Merge` refuse to leave its base
  would break that navigation for every caller to fix a problem only providers have.
- **`Merge`'s own bugs are fixed regardless.** Its prefix test now requires a segment boundary
  and is ordinal (a culture-aware match can succeed across ignorable characters with a matched
  length that does not line up with a separator, so it cannot decide a boundary); it honors
  `ignoreCase`, which it used to ignore. A leading `..` can no longer remove the base's root —
  it used to, so `Merge("/srv/public", "../../../x")` returned the relative path `x` — and a
  merged path keeps its root exactly instead of doubling it into a UNC-shaped `//srv/...`. Only
  an exact `..` segment counts as a parent reference, so `..config` is an ordinary name.
- **Rejected: a containment flag on `Merge`, or a public lexical `TryResolveWithin` on
  `FileSystemPath`.** A lexical check in a platform-neutral path type is the wrong primitive for
  host paths. Windows maps a final `NUL` segment to the `\\.\NUL` device and trims trailing dots
  and spaces, so a path that is lexically under the root can open something that is not. Only the host's normalization (`Path.GetFullPath`) answers "what will actually be
  opened", and that belongs in the provider that opens it. A public lexical API in Core would
  invite every consumer to treat it as sufficient for physical paths.
- **Rejected: returning `false` from `Exists` for an outside path.** See step 3 above; it also
  makes a refused path indistinguishable from a missing one, which hides misuse.

### Consumers

`FileSystemPath.Merge` (including the `+` operator, which calls it): the in-memory provider's
entry paths and tree walks, which merge one plain segment at a time (unchanged),
`IsolatedStoragePathHelper.ToAbsolute` (merges onto `/`; a leading `..` now throws
`ArgumentException` where it used to resolve to the store root), `FileSystemExtensions`
(`Exists` and `CreateSubdirectory` on a directory: in-root `..` navigation keeps working — and
now works on Unix too, where the doubled `//` root used to make the provider reject it — while the
provider refuses whatever leaves its root), and `FileSystemConfigurationProvider`, which merges a
relative file name onto the root for its watch glob (unchanged).

`PhysicalFileSystem` path members: `Web.StaticFiles` (gates its own request paths and passes
mount-relative paths; it also catches `FileSystemException`, so a refusal is a 404),
`Web.Hosting` and `Database.Hosting` (read-only content roots for `appsettings*.json`),
`Database.Storage` (roots a provider at a file's own directory and passes the bare file name), and
`Configuration.FileSystem`. None passes a path that leaves its root, so none changes behavior.

## Adding a new provider

1. Create `Assimalign.Cohesion.FileSystem.<Name>/src/` and `tests/` under
   `libraries/FileSystem/`.
2. Implement `IFileSystem` (typically using helper types in `Internal/` for
   the directory / file / info wrappers). Route every path-taking member through
   one containment check that follows *Root containment*, and test escapes
   against every member, as the physical and in-memory containment tests do.
3. Add a `FileSystemFactoryBuilder` extension named `Add<Name>FileSystem`.
4. Create `tests/<Name>FileSystemStandardTests.cs` inheriting
   `FileSystemStandardTests`, plus any provider-specific tests in a
   separate file.
5. Add an entry to `.github/workflows/library-filesystem.yml`'s matrix.
6. Add the project to `$script:CohesionReleaseLibrary` in
   `installer/scripts/modules/CohesionPackaging.psm1`; a provider ships as an
   ordinary NuGet package. It needs no framework entry: `Assimalign.Cohesion.App`
   derives its members from its kernel roots (`FileSystem.Physical` is one),
   and an area framework carries a provider only through a
   `CohesionFrameworkAssembly` line in that area's
   `resources/<Area>/Assimalign.Cohesion.<Area>.Runtime/Directory.Build.props`.
7. Update `libraries/FileSystem/README.md` and add per-package
   `README.md` + `docs/{OVERVIEW,DESIGN}.md`. Update
   `Assimalign.Cohesion.FileSystem/docs/COMPATIBILITY.md` with the
   new row.
