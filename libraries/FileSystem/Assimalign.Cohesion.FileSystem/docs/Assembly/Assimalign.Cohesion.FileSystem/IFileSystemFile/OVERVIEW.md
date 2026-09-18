# IFileSystemFile

Namespace: `Assimalign.Cohesion.FileSystem`  
Assembly: `Assimalign.Cohesion.FileSystem`

Represents a file returned by `IFileSystem.GetFile` or `IFileSystem.CreateFile`. Extends
`IFileSystemInfo` with `Size`, `Name`, `Directory`, `Watch()`, and the existing `Open`
stream overloads. There is no public constructor.

## OpenHandle

```csharp
IFileSystemFileHandle OpenHandle(FileMode fileMode, FileAccess fileAccess, FileShare fileShare);
```

Opens the file for positional reads and writes, length control, and durability-aware flush.
`fileMode` controls opening or creation, `fileAccess` selects reading and/or writing, and
`fileShare` controls other opens. The returned
[`IFileSystemFileHandle`](../IFileSystemFileHandle/OVERVIEW.md) belongs to the caller and must
be disposed with `using` or `await using`.

Providers enforce their file modes, access, sharing, and read-only rules. Opening can fail
when the file cannot be opened with those settings. Operations on the handle expose argument,
access, I/O, cancellation, and disposal errors. In particular, a handle whose
`SupportsDurableFlush` is `false` throws `NotSupportedException` on durable flush, so a
storage engine cannot silently lose a durability guarantee.

## Existing stream surface

`Open()`, `Open(FileMode)`, `Open(FileMode, FileAccess)`, and
`Open(FileMode, FileAccess, FileShare)` continue returning caller-owned `Stream` instances
with unchanged behavior. Sequential Configuration and Web.StaticFiles consumers continue to
use those methods without changes.
