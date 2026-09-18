# Assimalign.Cohesion.FileSystem.Physical — Design

## Design intent

A thin adapter that lets callers use `IFileSystem` on top of the real disk.
The provider is intentionally a single root: pass an absolute path, get a
file system rooted there. No virtual mounts, no glob filters at the
provider level — for those, use the Aggregate provider.

## Positional file handles and durability

`IFileSystemFile.OpenHandle(FileMode, FileAccess, FileShare)` returns an owned
`IFileSystemFileHandle` for page-oriented storage. It opens a `SafeFileHandle`
with `File.OpenHandle` and performs reads, writes, length queries, and resizing
through `System.IO.RandomAccess`. Independent offsets can be used concurrently
without moving a shared cursor. The existing `Open` overloads still return
streams unchanged: `Stream` alone supplies neither concurrent positional I/O
nor a durability-aware flush, which a storage engine needs for commit recovery.

`SupportsDurableFlush` is `true`. `Flush(durable: true)` calls
`RandomAccess.FlushToDisk`, and returns only when that operation completes.
There is no managed write buffer, so `Flush(durable: false)` has no work to do.
`FlushAsync` checks cancellation before starting; a durable flush uses the same
synchronous runtime primitive because .NET has no asynchronous durable flush.
An I/O error propagates to the caller rather than being downgraded to success.

The family contract requires `NotSupportedException` for durable flush whenever
`SupportsDurableFlush` is `false`. This physical provider can honor the request;
non-durable providers must fail loudly because acknowledging a commit without
durable data prevents the storage engine from recovering that commit.

The caller disposes each handle, synchronously or asynchronously. Disposal
releases the native handle and later operations throw `ObjectDisposedException`.
Mode, access, sharing, and read-only-provider checks still apply. As with
`File.OpenHandle`, `FileMode.Append` opens or creates a write-only handle; writes
use the explicitly supplied offsets rather than a stream's append cursor.
All implementation paths use .NET 10 public APIs without reflection and retain
NativeAOT compatibility.

## Path translation

Every aggregate-style `FileSystemPath` is merged onto the root via
`RootDirectory.Path.Merge(path)` before any `System.IO` call. Result: the
provider never escapes its root, even when callers pass `..`-suffixed
relative paths.

```csharp
var fs = new PhysicalFileSystem(new PhysicalFileSystemOptions
{
    Root = "/var/cohesion"
});

fs.CreateFile("config/app.json");   // -> /var/cohesion/config/app.json
fs.Exists("missing/../app.json");   // -> /var/cohesion/app.json (Merge resolves ..)
```

## Size reporting

`Size`, `SpaceAvailable`, `SpaceUsed` are sourced from the partition's
`DriveInfo`, not the root directory's recursive footprint. That's
intentional — the provider can't efficiently maintain a recursive size
without walking the tree on every query, and callers that care about
directory-scoped usage can compute it through enumeration.

## Auto-parent creation

`System.IO.FileInfo.Create` throws `DirectoryNotFoundException` if any
intermediate directory is missing. The provider explicitly calls
`info.Directory.Create()` before delegating to `Create()` so the public
contract (auto-create on `CreateFile("a/b/c/leaf.txt")`) holds.

`CopyFile` and `Move` apply the same fix-up on the destination side. The
source-side existence check happens first so a missing source still
throws `NotFound` rather than silently failing.

## Watch

`PhysicalFileSystemChangeToken` wraps a `FileSystemWatcher`. The provider
keeps a reference to the underlying watcher so disposal of the token also
disposes the watcher. There is no glob filtering at the provider level
beyond what the OS reports — callers wanting more precise filtering should
use the `Assimalign.Cohesion.FileSystem.Globbing` package on top.

## Exception mapping

Every `System.IO` exception is caught in the provider and re-thrown as
`FileSystemException` with the appropriate `FileSystemErrorCode` (see
`docs/OVERVIEW.md` for the matrix). The original exception is preserved
through `InnerException` for callers that need it.

## Layout

```
src/
  PhysicalFileSystem.cs           public provider
  PhysicalFileSystemOptions.cs    public options bag
  Extensions/
    PhysicalFileSystemExtensions.cs
  Internal/
    PhysicalFileSystemDirectory.cs
    PhysicalFileSystemFile.cs
    PhysicalFileSystemFileHandle.cs
    PhysicalFileSystemInfo.cs
    PhysicalFileSystemChangeToken.cs
    FileSystemInfoHelper.cs
tests/
  PhysicalFileSystemTests.cs         provider-specific behavior
  PhysicalFileSystemFileHandleTests.cs positional I/O, durability, and disposal
  PhysicalFileSystemStandardTests.cs inherits the shared contract suite
  Shared/FileSystemStandardTests.cs  (linked from root package)
```

## Cross-platform behavior

- Path separators: incoming `/` is honored by `System.IO` on every OS.
  Windows reports `\` separators in some return paths (e.g. enumeration);
  the provider returns the raw `System.IO` strings via `FileSystemPath`
  conversion which keeps the `/` convention for callers.
- Case sensitivity follows the host file system. The provider does not
  attempt to normalize.
- File attributes (`Hidden`, `System`) can be excluded from enumeration via
  `PhysicalFileSystemOptions.IgnoreAttributes`.
