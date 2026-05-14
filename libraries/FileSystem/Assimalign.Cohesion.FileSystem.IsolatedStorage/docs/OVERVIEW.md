# Assimalign.Cohesion.FileSystem.IsolatedStorage

## Summary

`IFileSystem` implementation backed by `System.IO.IsolatedStorage.IsolatedStorageFile`.
Provides per-user / per-assembly persistent storage without requiring the
caller to choose a directory on disk — the runtime picks an OS-appropriate
location (`%USERPROFILE%\AppData\IsolatedStorage` on Windows,
`~/.local/share/IsolatedStorage` on Linux, etc.).

## When to pick it

- Cross-platform user-scoped persistence where you don't want to write
  install-time path resolution code.
- Sandboxed scenarios (downloaded plugin, ClickOnce-like deployment) where
  isolated storage is the only writable surface.
- Tests that want a real persisted file system but with automatic cleanup
  (set `RemoveStoreOnDispose = true`).

## Public surface

| Type | Role |
|------|------|
| `IsolatedStorageFileSystem` | The provider. |
| `IsolatedStorageFileSystemOptions` | `Name`, `IsReadOnly`, `Scope`, evidence types, `IgnoreAttributes`, `RemoveStoreOnDispose`, `WatchPollInterval`. |
| `FileSystemFactoryBuilder.AddIsolatedStorageFileSystem` | Factory-builder extension. |

## Scope selection

The `Scope` option (default `User | Assembly`) maps to the matching
`IsolatedStorageFile.GetStore` overload. Evidence-type options
(`ApplicationEvidenceType`, `AssemblyEvidenceType`, `DomainEvidenceType`)
let callers override the default identity resolution when targeting a
specific scope.

## Watch semantics

`IsolatedStorageFile` does not expose native change notifications, so the
provider implements watch as polling. Cadence is configured via
`IsolatedStorageFileSystemOptions.WatchPollInterval`:

- Default `1s`. Each tick snapshots the directory tree (path → length +
  last-write-time) and diffs against the previous snapshot to surface
  `Created` / `Deleted` / `Changed` events.
- Set to `Timeout.InfiniteTimeSpan` to disable polling. `Watch` returns a
  noop token that never fires.

Rename detection is not attempted via polling. `OnRename` registrations
are accepted for API parity but never fire — subscribers needing rename
fidelity should observe paired `OnDelete` + `OnCreate` events.

## Unsupported capabilities

| Member | Behavior |
|--------|----------|
| `IFileSystemInfo.Attributes` (getter) | Throws `NotSupportedException` (IsolatedStorage does not expose `FileAttributes`). |
| `IFileSystemInfo.SetAttributes` | Throws `NotSupportedException`. |
| `IFileSystemEventToken.OnRename` | Registration accepted but callback never fires. |

These are deterministic and documented — callers see consistent failures
rather than silent no-ops.

## Test coverage

76 tests including:

- 32 contract tests from `FileSystemStandardTests`.
- Provider-specific tests for options validation, read-only enforcement
  (every write op throws `FileSystemException(ReadOnly)`), watch
  fan-out across polling intervals, glob filtering, dispose semantics,
  `RemoveStoreOnDispose` behavior, factory registration.
- Direct unit tests on `IsolatedStoragePathHelper` covering the
  bidirectional translation between `FileSystemPath` and the relative
  store-path strings expected by `IsolatedStorageFile`.
