# Assimalign.Cohesion.FileSystem.InMemory

## Summary

In-process `IFileSystem` implementation that stores every file's bytes in
managed memory. Designed for tests, ephemeral caches, and fixtures where a
real disk would be slow, leaky, or wrong. Configurable quota, synchronous
change notifications, and locking that mirrors Linux directory-locking
rules.

## When to pick it

- Unit / integration tests that need a real `IFileSystem` without touching
  the disk.
- Short-lived scratch space inside a single process.
- Anywhere data must vanish when the process exits.

## Public surface

| Type | Role |
|------|------|
| `InMemoryFileSystem` | The provider. Implements `IFileSystem`. |
| `InMemoryFileSystemOptions` | `Name`, `IsReadOnly`, `Size` quota (default 32 MB), `IgnoreCase`, `CultureInfo`, `RootPath`, `IgnoreAttributes`. |
| `FileSystemFactoryBuilder.AddInMemoryFileSystem` | Factory-builder extension for `Assimalign.Cohesion.FileSystem`. |

Internal directory / file / dispatcher / locking types are kept under
`src/Internal/` and not surfaced through the public API.

## Watch semantics

Synchronous. Mutations (`CreateFile`, `DeleteDirectory`, etc.) raise events
through `InMemoryFileSystemEventToken` before the call returns. No polling,
no `FileSystemWatcher`.

> **Known issue (pre-existing, tracked separately):** event paths include
> a doubled trailing segment (`/foo.txt/foo.txt`) because the
> `FileSystemEventArgs` is constructed with the entry's full path as the
> `Directory` argument. Aggregate provider watch tests work around this by
> asserting "a callback fires" rather than the exact path.

## Quota and read-only

- `InMemoryFileSystemOptions.Size` caps total used bytes. Writes that would
  exceed it throw `FileSystemException` with `FileSystemErrorCode.NotEnoughSpace`.
- `IsReadOnly = true` makes every mutating operation throw
  `FileSystemException` with `FileSystemErrorCode.ReadOnly`.

## Test coverage

109 tests including:

- 34 contract tests inherited from `FileSystemStandardTests` (path
  semantics, CRUD, copy/move, enumeration, deep nesting, large files,
  timestamps).
- 75 provider-specific tests covering quota enforcement, locking, change
  dispatch, lookup, options validation.
