# Assimalign.Cohesion.FileSystem

The root contract for the Cohesion file system family. Defines
`IFileSystem`, `IFileSystemFile`, `IFileSystemDirectory`,
`IFileSystemEventToken`, the `FileSystemFactoryBuilder` registration
surface, and the `FileSystemException` domain exception with explicit
error codes.

Pair this package with a concrete provider:

| Provider | Storage |
|----------|---------|
| `Assimalign.Cohesion.FileSystem.InMemory` | Process-memory dictionary tree |
| `Assimalign.Cohesion.FileSystem.Physical` | The host OS file system |
| `Assimalign.Cohesion.FileSystem.IsolatedStorage` | `System.IO.IsolatedStorage` |
| `Assimalign.Cohesion.FileSystem.Aggregate` | Multiple providers mounted at virtual paths |

Provider-agnostic contract tests live in `tests/Shared/FileSystemStandardTests.cs`
and are shared (via `<Compile Include …>`) into every concrete provider's
test project.

See `docs/OVERVIEW.md` and `docs/DESIGN.md` for details.