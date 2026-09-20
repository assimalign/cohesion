# Assimalign.Cohesion.FileSystem.IsolatedStorage — Design

## Design intent

Surface `System.IO.IsolatedStorage.IsolatedStorageFile` as an
`IFileSystem`. The underlying API is .NET's portable, per-user, per-
assembly sandboxed store — useful when you want persistent storage on
every supported OS without writing path-resolution code per platform.

## Positional file handles and durability

`IFileSystemFile.OpenHandle(FileMode, FileAccess, FileShare)` returns a caller-owned
`IFileSystemFileHandle` for page-oriented storage. Reads and writes supply their
own offsets; a private `SemaphoreSlim` covers each seek and I/O operation, length
query, resize, flush, and disposal. Concurrent calls cannot disturb one another's
offsets, including when synchronous and asynchronous calls are mixed. This
serializes I/O within one handle. The stream is private and never handed to callers.

The .NET 10 implementation of `IsolatedStorageFileStream.SafeFileHandle` throws
`IsolatedStorageException`, so the physical provider's `RandomAccess` route is
unavailable through supported handle access. Its public `Flush(bool)` does forward
to the backing `FileStream.Flush(bool)`, so this provider genuinely supports
durability and reports `SupportsDurableFlush == true`.
See the [.NET 10 runtime implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.IsolatedStorage/src/System/IO/IsolatedStorage/IsolatedStorageFileStream.cs).
No reflection, deprecated native-handle access, or physical store-path discovery
is needed; the implementation retains the store's public access boundaries and
NativeAOT compatibility.

`Flush(durable: true)` calls `IsolatedStorageFileStream.Flush(flushToDisk: true)`.
`FlushAsync` honors cancellation while waiting for the gate and before starting
the flush; its durable branch uses the synchronous durable primitive because
there is no asynchronous overload with that guarantee. The non-durable branch
uses the stream's ordinary `FlushAsync`. Failures propagate without degrading
the guarantee. The family contract requires `NotSupportedException` whenever
durable flush is requested from a handle reporting `SupportsDurableFlush == false`:
a storage engine must refuse a guarantee it cannot provide rather than acknowledge
commits it cannot recover.

The existing `Open` overloads remain unchanged. A plain `Stream` cannot express
concurrent positional operations or a durability-aware flush; the new handle
provides both without requiring a storage engine to downcast its stream. Mode,
access, sharing, and provider read-only checks are preserved. For handles,
`FileMode.Append` is normalized to write-only `OpenOrCreate`, matching
`File.OpenHandle`: each write still honors its explicit offset.

Synchronous and asynchronous disposal wait for the current operation, release the
owned isolated stream, and make subsequent operations throw `ObjectDisposedException`.
The gate remains available after disposal so waiting callers observe that contract
instead of racing disposal of the synchronization primitive.

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

Polling tokens implement `IDisposable` without changing `IFileSystemEventToken`.
Keep the token's lifetime scoped explicitly with `using var watchScope = token as IDisposable`;
disposing an individual callback registration only unsubscribes that callback, not the watch.
The file system retains outstanding tokens as a fallback and disposes them before closing or
removing the store. A disposed token removes itself from that ownership list. Token and provider
disposal are idempotent in either order, including concurrent calls. Creation and ownership
registration are serialized with provider disposal so a late watch cannot escape cleanup.

Snapshot I/O and token disposal share a gate: disposal waits for an active scan to finish, stops
the timer, and prevents queued ticks from accessing the store. Notifications run outside that
gate so a callback may dispose its own token or provider without deadlocking. A subscriber already
being invoked may finish after disposal; dispatch checks the stopped state before each remaining
subscriber. No callback can restart the timer. The timer is created through `TimeProvider.System`;
an internal constructor accepts a controllable provider for deterministic lifetime tests.

Snapshot streams use `FileShare.ReadWrite | FileShare.Delete`. Polling must allow callers to
delete or replace files even while the scan samples their lengths. A disappearing file is a
normal snapshot race, handled by the next diff rather than an exclusive polling handle.

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
    IsolatedStorageFileSystemFileHandle.cs
    IsolatedStorageFileSystemInfo.cs
    IsolatedStorageFileSystemNoopEventToken.cs
    IsolatedStorageFileSystemPollingEventToken.cs
    IsolatedStoragePathHelper.cs
  Properties/
    AssemblyInfo.cs   (InternalsVisibleTo declaration)
tests/
  IsolatedStorageFileSystemTests.cs          provider-specific behavior
  IsolatedStorageFileSystemFileHandleTests.cs positional I/O, durability, and disposal
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
