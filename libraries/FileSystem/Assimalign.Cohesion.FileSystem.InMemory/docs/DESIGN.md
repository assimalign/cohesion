# Assimalign.Cohesion.FileSystem.InMemory — Design

## Design intent

A test-grade `IFileSystem` whose backing store lives entirely in process
memory. Identical public surface to `PhysicalFileSystem` so consumers can
swap between the two without behavior change.

## Storage model

- The provider holds a single root `InMemoryFileSystemDirectory`. Every
  directory keeps a `Dictionary<FileSystemPath, InMemoryFileSystemInfo>`
  of children plus a synthetic `Lookup` view used by enumeration / traversal.
- Each file owns an `InMemoryFileContent` chunk-buffer. Streams (`Open`)
  read and write into that buffer; `Size` reflects the buffer's current
  length.
- Total space used is tracked at the file-system level. Writes that would
  push past `InMemoryFileSystemOptions.Size` throw
  `FileSystemException(NotEnoughSpace)`.

## Locking

Modeled on the Linux kernel's directory-locking rules
(https://www.kernel.org/doc/Documentation/filesystems/directory-locking).
Every mutation acquires explicit locks on the file system + parent
directories + target entry before touching state, then releases them via
`InMemoryFileSystemLockManager.Dispose()`. Read-only operations
(`Exists`, `GetInfo`) take a weaker `Delete` lock so they coexist with
concurrent reads but block in-flight deletions.

## Path handling

- `InMemoryFileSystemOptions.RootPath` defaults to `/` and rejects
  `..`-prefixed relative paths in the setter.
- The provider stores `IgnoreCase` (default true) and `CultureInfo`
  (default invariant) and uses them through `FileSystemPath.Equals` so
  lookups behave the same on Linux and Windows.
- Incoming paths are normalized via `RootDirectory.Path.Merge(path, culture, ignoreCase)`
  before any tree-walking — relative paths are rooted at the file system's
  root, absolute paths are checked for scope.

## Watch dispatcher

`InMemoryFileSystemDispatcher` exposes `Created`/`Deleted`/`Changed`/`Renamed`
events. Each mutation raises the appropriate event after the state change
completes; `InMemoryFileSystemEventToken` subscribes to the dispatcher and
filters by `Glob` and registration type.

The synchronous nature makes the in-memory provider easy to assert against
in tests — there is no race between mutation and notification.

## Layout

```
src/
  InMemoryFileSystem.cs              public provider
  InMemoryFileSystemOptions.cs       public options bag
  InMemoryFileSystemLockHandle.cs    public lock-bearer base class
  Extensions/                        FileSystemFactoryBuilder extensions
  Internal/
    InMemoryFileSystemDirectory.cs
    InMemoryFileSystemFile.cs
    InMemoryFileSystemInfo.cs
    InMemoryFileSystemDispatcher.cs
    InMemoryFileSystemEventToken.cs
    InMemoryFileSystemLockManager.cs
    IO/
      InMemoryFileContent.cs
      InMemoryFileStream.cs
tests/
  FileSystemTests.cs            provider-specific behavior
  Shared/FileSystemStandardTests.cs (linked from root package)
```

## Examples

```csharp
// Default: 32 MB quota, ignore-case, root at "/".
using var fs = new InMemoryFileSystem(new InMemoryFileSystemOptions());

// Read-only fixture seeded ahead of time.
using var readonlyFs = new InMemoryFileSystem(new InMemoryFileSystemOptions
{
    IsReadOnly = true,
});

// Via the factory builder.
using var factory = new FileSystemFactoryBuilder()
    .AddInMemoryFileSystem(options =>
    {
        options.Name = "scratch";
        options.Size = Size.FromMegabytes(8);
    })
    .Build();
IFileSystem scratch = factory.Create("scratch");
```
