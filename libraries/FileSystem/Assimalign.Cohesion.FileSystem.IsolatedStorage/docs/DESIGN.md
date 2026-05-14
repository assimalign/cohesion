# Assimalign.Cohesion.FileSystem.IsolatedStorage — Design

## Design intent

Surface `System.IO.IsolatedStorage.IsolatedStorageFile` as an
`IFileSystem`. The underlying API is .NET's portable, per-user, per-
assembly sandboxed store — useful when you want persistent storage on
every supported OS without writing path-resolution code per platform.

## Path translation

`IsolatedStoragePathHelper` is the single source of truth for translating
between aggregate-style paths (`/foo/bar.txt`, '/' separated, rooted) and
the store-side relative path strings expected by `IsolatedStorageFile`
(`foo/bar.txt`, no leading separator).

```csharp
IsolatedStoragePathHelper.ToAbsolute("foo")        // "/foo"
IsolatedStoragePathHelper.ToStorePath("/foo")      // "foo"
IsolatedStoragePathHelper.ToStorePath("/")         // ""
IsolatedStoragePathHelper.FromStorePath("foo\\bar")// "/foo/bar"  (Windows-style separators normalized)
IsolatedStoragePathHelper.ChildSearchPattern("/foo") // "foo/*"
```

The helper is `internal` and surfaced to the test assembly through
`InternalsVisibleTo` (key-free — see the PR2 hardening for the CS0281
rationale).

## Scope resolution

`OpenStore` picks the right `IsolatedStorageFile.GetStore` overload based
on the configured `IsolatedStorageScope`:

| Scope contains | Overload used |
|----------------|----------------|
| `Application` | `GetStore(scope, applicationEvidenceType)` |
| `Domain` | `GetStore(scope, domainEvidenceType, assemblyEvidenceType)` |
| Otherwise | `GetStore(scope, assemblyEvidenceType)` |

The default (`User | Assembly`) is the same store as
`IsolatedStorageFile.GetUserStoreForAssembly`.

## Polling-based watch

`IsolatedStorageFile` exposes no native change notifications. The polling
event token uses a `System.Threading.Timer`:

1. On construction, snapshot the directory tree.
2. Each tick (default 1s): walk the tree, compare against the previous
   snapshot, dispatch `Created` / `Deleted` / `Changed` events.
3. A CAS guard (`Interlocked.CompareExchange(ref _polling, 1, 0)`)
   prevents re-entrant ticks if a poll takes longer than the interval.
4. Subscriber callbacks run inside `try/catch` so a faulty subscriber
   can't kill the timer loop.

`Dispose()` stops the timer first, then clears the subscriber list, so
no callbacks fire after the call returns. The file system tracks every
token it hands out and disposes them in its own `Dispose` for safety.

Rename detection requires correlating a delete + create pair within a
single tick, which is fragile across providers. The provider declines to
guess: `OnRename` registrations are accepted but never fire. Callers
needing rename fidelity should subscribe to `OnDelete` + `OnCreate` and
correlate themselves.

## Auto-parent creation

`IsolatedStorageFile.CreateFile` throws if the destination directory
doesn't exist. The provider explicitly creates the parent chain first so
the public contract (auto-create on `CreateFile("a/b/c/leaf.txt")`) holds.

## Lifetime

- `Dispose()` stops outstanding poll timers, optionally calls
  `IsolatedStorageFile.Remove()` if `RemoveStoreOnDispose = true`, then
  disposes the underlying handle.
- After `Dispose`, every public member throws `ObjectDisposedException`.
- Idempotent — calling `Dispose` twice does nothing on the second call.

## Layout

```
src/
  IsolatedStorageFileSystem.cs           public provider
  IsolatedStorageFileSystemOptions.cs    public options bag
  Extensions/
    IsolatedStorageFileSystemExtensions.cs
  Internal/
    IsolatedStorageFileSystemDirectory.cs
    IsolatedStorageFileSystemFile.cs
    IsolatedStorageFileSystemInfo.cs
    IsolatedStorageFileSystemNoopEventToken.cs
    IsolatedStorageFileSystemPollingEventToken.cs
    IsolatedStoragePathHelper.cs
  Properties/
    AssemblyInfo.cs   (InternalsVisibleTo declaration)
tests/
  IsolatedStorageFileSystemTests.cs          provider-specific behavior
  IsolatedStorageFileSystemStandardTests.cs  inherits the shared contract suite
  IsolatedStorageFileSystemTestFixture.cs    helper for store cleanup
  IsolatedStoragePathHelperTests.cs          direct unit tests
  AssemblyInfo.cs                            [assembly: CollectionBehavior(DisableTestParallelization = true)]
  Shared/FileSystemStandardTests.cs          (linked from root package)
```

The test assembly disables xUnit parallelization because the per-user
isolated store is shared across the test process — each test would
otherwise stomp on others' files. The test fixture clears the store
recursively before constructing each provider.
