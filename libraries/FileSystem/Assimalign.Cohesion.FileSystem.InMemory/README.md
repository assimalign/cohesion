# Assimalign.Cohesion.FileSystem.InMemory

In-process `IFileSystem` implementation that stores every file's bytes in
managed memory. Designed for tests, ephemeral caches, and fixtures.

```csharp
using Assimalign.Cohesion.FileSystem;

using var factory = new FileSystemFactoryBuilder()
    .AddInMemoryFileSystem(options =>
    {
        options.Name = "scratch";
        options.Size = Size.FromMegabytes(8);
    })
    .Build();

IFileSystem fs = factory.Create("scratch");
fs.CreateFile("hello.txt");
```

- Synchronous watch events (no polling, no `FileSystemWatcher`).
- Configurable quota — writes that would exceed `Size` throw
  `FileSystemException(NotEnoughSpace)`.
- Optional read-only mode.

See `docs/OVERVIEW.md` and `docs/DESIGN.md` for details.
