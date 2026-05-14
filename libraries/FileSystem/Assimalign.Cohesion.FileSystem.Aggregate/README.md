# Assimalign.Cohesion.FileSystem.Aggregate

`IFileSystem` implementation that composes multiple mounted providers
under a single virtual namespace. Routes operations to the mount with
the longest matching path prefix.

```csharp
using Assimalign.Cohesion.FileSystem;

using var aggregate = new AggregateFileSystemBuilder()
    .Mount("/data",  new PhysicalFileSystem(new PhysicalFileSystemOptions { Root = "/var/data" }), ownsFileSystem: true)
    .Mount("/cache", new InMemoryFileSystem(new InMemoryFileSystemOptions()),                       ownsFileSystem: true)
    .Build();

aggregate.CreateFile("/cache/transient.bin"); // routed to InMemory
aggregate.CreateFile("/data/payload.bin");    // routed to Physical
```

- Longest-prefix routing with segment-boundary check (so `/data` never
  matches `/database`).
- Synthetic read-only directories for intermediate path segments that
  lead toward a mount but aren't themselves a mount root.
- Cross-provider `CopyFile` / `Move` stream the source's bytes into a
  freshly-created destination on the target provider.
- Returned `IFileSystemFile` / `IFileSystemDirectory` instances have
  aggregate-space `Path` values and point their `FileSystem` property
  back at the aggregate.
- Watch fan-in across every mount, with event paths remapped into
  aggregate-space before dispatch.
- Disposal cascades to mounts registered with `ownsFileSystem: true`.

See `docs/OVERVIEW.md` and `docs/DESIGN.md` for details.
