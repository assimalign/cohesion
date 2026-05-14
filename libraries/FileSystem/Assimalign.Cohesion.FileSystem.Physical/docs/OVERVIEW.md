# Assimalign.Cohesion.FileSystem.Physical

## Summary

`IFileSystem` implementation backed by the OS file system via `System.IO`.
Rooted at a configurable absolute path. Reports drive-level size and free
space through `System.IO.DriveInfo` for the partition that holds the root.

## When to pick it

- Production workloads that persist to a real directory.
- Anywhere `System.IO.FileStream` semantics are required (memory-mapped
  access, `FileShare`, `FileMode.Open` matrix).
- Cross-process scenarios where another process needs to see the same files.

## Public surface

| Type | Role |
|------|------|
| `PhysicalFileSystem` | The provider. Wraps a single root `DirectoryInfo`. |
| `PhysicalFileSystemOptions` | `Root` (required), `Name`, `IsReadOnly`, `IgnoreAttributes`. |
| `FileSystemFactoryBuilder.AddPhysicalFileSystem` | Factory-builder extension. |

Directory / file / info / change-token wrappers live under `src/Internal/`
and are not surfaced through the public API.

## Watch semantics

Backed by `System.IO.FileSystemWatcher`. Latency depends on the host OS
(inotify on Linux, ReadDirectoryChangesW on Windows, kqueue on macOS).

## Exception mapping

The provider translates OS exceptions into `FileSystemException` with the
matching `FileSystemErrorCode`:

| Source | Mapped code |
|--------|-------------|
| `UnauthorizedAccessException`, `IOException` with sharing/locking HResult | `AccessDenied` |
| `PathTooLongException` | `PathTooLong` |
| `FileNotFoundException` | `NotFound` |
| `DirectoryNotFoundException` | `NotFound` |
| `IOException` with conflict HResult | `Conflict` |

## Auto-create semantics

`CreateFile("a/b/c/leaf.txt")` auto-creates the intermediate directory
chain to match the InMemory contract. The bare `FileInfo.Create()` does
not — the provider explicitly calls `parent.Create()` first.

`CopyFile` and `Move` also auto-create the destination's parent chain
before delegating to `File.Copy` / `File.Move`.

## Test coverage

66 tests including:

- 34 contract tests from `FileSystemStandardTests`.
- 32 provider-specific tests covering exception mapping for each OS error,
  options validation, read-only enforcement, enumeration filtering by
  attributes.
