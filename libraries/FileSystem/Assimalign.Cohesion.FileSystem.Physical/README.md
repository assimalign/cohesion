# Assimalign.Cohesion.FileSystem.Physical

`IFileSystem` implementation backed by the OS file system via `System.IO`.
Rooted at a configurable absolute path.

```csharp
using Assimalign.Cohesion.FileSystem;

using var factory = new FileSystemFactoryBuilder()
    .AddPhysicalFileSystem(options =>
    {
        options.Root = "/var/cohesion";
    })
    .Build();

IFileSystem fs = factory.Create("PhysicalFileSystem");
fs.CreateFile("config/app.json"); // -> /var/cohesion/config/app.json
```

- Auto-creates intermediate parent directories on `CreateFile` and the
  destination side of `CopyFile` / `Move`.
- Size and free space sourced from `System.IO.DriveInfo` for the
  partition holding the root.
- Watch backed by `System.IO.FileSystemWatcher` (latency is OS-dependent).
- OS exceptions are mapped to `FileSystemException` with the matching
  `FileSystemErrorCode`.

See `docs/OVERVIEW.md` and `docs/DESIGN.md` for details.
