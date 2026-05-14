# Assimalign.Cohesion.FileSystem.Aggregate — Design

## Design intent

Compose multiple `IFileSystem` instances into a single virtual file system
without changing the caller-facing contract. Path resolution uses
longest-prefix matching against a pre-sorted mount table; results are
re-wrapped so paths flowing back to callers live in aggregate-space, not
provider-relative form.

## Routing

`AggregateRouter.SortByLongestPrefix` sorts the mount table once at
construction so resolution is a single forward scan:

```csharp
foreach (var mount in sortedMounts)
{
    if (mount.MountPath == "/") return mount;       // root mount matches everything
    if (text.Equals(mount.MountPath)) return mount; // exact mount root
    if (text.StartsWith(mount.MountPath) && text[mount.MountPath.Length] == '/')
        return mount;                               // longest matching prefix
}
return null;                                        // path lives in synthetic space
```

The segment-boundary check (`text[mount.MountPath.Length] == '/'`) is
critical — without it, mount `/data` would erroneously claim `/database`.

## Synthetic root

When no mount is at `/`, the aggregate's root is an
`AggregateSyntheticDirectory`. Its `GetDirectories()` enumerates the first
segment of every registered mount path:

```csharp
// Mounts: /data/cache, /var/log
// Root.GetDirectories() returns synthetic dirs for "data" and "var"
// Root.GetDirectory("data").GetDirectories() returns the wrapped /data/cache mount
```

Synthetic directories are read-only. Every mutating op throws
`FileSystemException(ReadOnly)` to keep the intent explicit.

## Path wrapping

`AggregateFileSystemFile` and `AggregateFileSystemDirectory` wrap entries
from mounted providers and translate paths in both directions:

- `Path` returns the aggregate-side absolute path.
- `Parent`, `Directory`, `GetDirectories`, `GetFiles`, `EnumerateFileSystem`
  re-wrap child entries with their aggregate-space paths.
- `CreateDirectory` / `CreateFile` / `Delete*` route back through the
  aggregate so mount resolution and read-only checks apply.
- `FileSystem` always points back at the aggregate, never the underlying
  mount.

Translation lives in `AggregateMount.ToAggregatePath` /
`ToProviderPath` with theory-driven unit tests so the conversion behavior
is locked down.

## Cross-provider Copy / Move

```csharp
public void CopyFile(FileSystemPath source, FileSystemPath destination)
{
    var sourceMount = router.Resolve(sourceAbs);
    var destMount = router.Resolve(destAbs);

    if (ReferenceEquals(sourceMount.FileSystem, destMount.FileSystem))
    {
        // Same provider: delegate so it can optimize (timestamps, etc.).
        sourceMount.FileSystem.CopyFile(sourceMount.ToProviderPath(sourceAbs),
                                        destMount.ToProviderPath(destAbs));
        return;
    }

    // Different providers: stream the bytes.
    var sourceFile = sourceMount.FileSystem.GetFile(sourceMount.ToProviderPath(sourceAbs));
    var destFile = destMount.FileSystem.CreateFile(destMount.ToProviderPath(destAbs));
    StreamContents(sourceFile, destFile);
}
```

`Move` follows the same shape but deletes the source after the destination
write completes.

## Watch fan-in

`AggregateFileSystemEventToken` subscribes to every mount with a catch-
all `**` glob (mount-side filtering is intentionally disabled because
some providers — notably InMemory — default a null glob to "watch the
directory's own path only"). When a mount fires an event, the fan-in
remaps the path back into aggregate-space and only then applies the
caller-supplied glob.

`OnRename` remaps both the old and new paths and dispatches a single
remapped `FileSystemRenameEvent`.

The fan-in tracks every mount subscription and disposes them all when the
aggregate token is disposed. The aggregate file system also tracks every
token it hands out and disposes them in its own `Dispose` for safety.

## Disposal

```csharp
public void Dispose()
{
    foreach (var mount in _mountsSorted)
    {
        if (mount.OwnsFileSystem)
        {
            try { mount.FileSystem.Dispose(); } catch { /* best-effort */ }
        }
    }
}
```

Only `ownsFileSystem: true` mounts cascade. The default is `false`
because in most composition scenarios the caller already manages the
underlying providers' lifetimes (e.g. they're registered on the same
`FileSystemFactoryBuilder` as the aggregate).

## Layout

```
src/
  AggregateFileSystem.cs           public provider
  AggregateFileSystemOptions.cs    public options bag (internal mounts list)
  AggregateFileSystemBuilder.cs    single-use fluent builder
  Extensions/
    AggregateFileSystemExtensions.cs   FileSystemFactoryBuilder extension
  Internal/
    AggregateMount.cs                  mount record + path translation
    AggregateRouter.cs                 longest-prefix routing helpers
    AggregateFileSystemDirectory.cs    wrapped directory
    AggregateFileSystemFile.cs         wrapped file
    AggregateSyntheticDirectory.cs     synthetic intermediate directory
    AggregateFileSystemEventToken.cs   watch fan-in token
  Properties/
    AssemblyInfo.cs   (InternalsVisibleTo)
tests/
  AggregateFileSystemTests.cs            provider-specific behavior
  AggregateFileSystemStandardTests.cs    inherits shared contract suite
  AggregateRouterTests.cs                router primitives
  Shared/FileSystemStandardTests.cs      (linked from root package)
```
