# Assimalign.Cohesion.FileSystem

The Cohesion file system family. A single `IFileSystem` contract surfaces files,
directories, change notifications, and enumeration across multiple backing
storage strategies. Pick the provider that matches the storage you actually
have; the public surface is identical.

## Packages

| Package | Backing storage | When to pick it |
|---------|-----------------|-----------------|
| `Assimalign.Cohesion.FileSystem` | none (contract only) | Defining APIs that take an `IFileSystem` without binding to a backend. |
| `Assimalign.Cohesion.FileSystem.InMemory` | in-process dictionary tree with byte buffers | Tests, fixtures, short-lived caches, ephemeral scratch space. Configurable size quota. Synchronous watch events. |
| `Assimalign.Cohesion.FileSystem.Physical` | the host OS file system via `System.IO` | Production storage rooted at a real directory. Reports `DriveInfo`-backed size/free space. |
| `Assimalign.Cohesion.FileSystem.IsolatedStorage` | `System.IO.IsolatedStorage.IsolatedStorageFile` | Per-user / per-assembly persistence on Windows + cross-platform. Watch is implemented via configurable polling. |
| `Assimalign.Cohesion.FileSystem.Aggregate` | other `IFileSystem` instances mounted at virtual paths | Composition — mount InMemory at `/cache`, Physical at `/data`, etc. Longest-prefix routing with synthetic intermediate directories. |
| `Assimalign.Cohesion.FileSystem.Globbing` | n/a (path-matching helpers) | Pattern matching against `FileSystemPath` values. Used by the providers' watch APIs. |

## Quick start

```csharp
using Assimalign.Cohesion.FileSystem;

// Pick a provider by name from a factory:
using var factory = new FileSystemFactoryBuilder()
    .AddInMemoryFileSystem(options => options.Name = "scratch")
    .AddPhysicalFileSystem(options => options.Root = "/var/cohesion")
    .Build();

IFileSystem fs = factory.Create("scratch");
var file = fs.CreateFile("hello.txt");
using (var stream = file.Open(FileMode.Open, FileAccess.Write))
{
    stream.Write(System.Text.Encoding.UTF8.GetBytes("payload"));
}
```

## Aggregate routing

The Aggregate provider lets you mount multiple backends behind one
`IFileSystem`:

```csharp
using var aggregate = new AggregateFileSystemBuilder()
    .Mount("/data",  new PhysicalFileSystem(new PhysicalFileSystemOptions { Root = "/var/data" }), ownsFileSystem: true)
    .Mount("/cache", new InMemoryFileSystem(new InMemoryFileSystemOptions()),                       ownsFileSystem: true)
    .Build();

aggregate.CreateFile("/cache/transient.bin"); // routed to InMemory
aggregate.CreateFile("/data/payload.bin");    // routed to Physical
```

Path resolution uses longest-prefix matching. Intermediate path segments that
sit above a mount surface as synthetic read-only directories so enumeration
and traversal still work.

## Contract tests

Provider-agnostic contract tests live in
`Assimalign.Cohesion.FileSystem/tests/Shared/FileSystemStandardTests.cs` and
are shared (via `<Compile Include …>`) into every provider's test project.
Adding a new provider means inheriting from `FileSystemStandardTests`,
overriding `GetFileSystem()`, and watching the 32 contract tests pass.

## Watch semantics

| Provider | Watch implementation |
|----------|----------------------|
| InMemory | Synchronous dispatch. Events fire inside `CreateFile`/`DeleteFile`/etc. before the call returns. |
| Physical | Backed by `FileSystemWatcher`. Platform-dependent latency. |
| IsolatedStorage | Polling (`IsolatedStorageFileSystemOptions.WatchPollInterval`, default 1s). `OnRename` registrations are accepted but never fire — see the package's `docs/DESIGN.md` for the rationale. |
| Aggregate | Fan-in across mount tokens. Events are remapped from provider-space paths back into aggregate-space before dispatch. |

## Documentation

- Per-package `docs/OVERVIEW.md` and `docs/DESIGN.md` cover the implementation details.
- `docs/COMPATIBILITY.md` (under `Assimalign.Cohesion.FileSystem/`) lists OS + runtime + NativeAOT support per provider.
