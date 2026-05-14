# Assimalign.Cohesion.FileSystem

## Summary

The root contract package for the Cohesion file-system family. Defines the
`IFileSystem` surface (files, directories, events, enumeration) and the
factory used to wire concrete providers into an application by name.

## Status

Implemented. Zero `NotImplementedException` markers in production code. 39
unit tests live in this package; 32 of those make up the provider-agnostic
contract suite shared into every concrete provider via `<Compile Include …>`.

## Public surface

| Type | Role |
|------|------|
| `IFileSystem` | Contract for a directory-and-file tree. Inherits `IDisposable` + `IAsyncDisposable`. |
| `IFileSystemFile`, `IFileSystemDirectory`, `IFileSystemInfo` | Entry contracts returned from the file system. |
| `IFileSystemEventToken` | Change-notification token. Inherits `IChangeToken`. |
| `IFileSystemFactory` | Resolves a named `IFileSystem` from a builder-registered table. |
| `FileSystemFactoryBuilder` | Single-use builder with case-insensitive name registration, duplicate rejection, lazy instantiation. |
| `FileSystemException` + `FileSystemErrorCode` | Domain exception with explicit error codes (`NotFound`, `Conflict`, `ReadOnly`, `AccessDenied`, `NotEnoughSpace`, `PathTooLong`, `PathInUse`, `Other`). |
| `FileSystemPath` | Lives in `Assimalign.Cohesion.Core` (`System.IO` namespace) — '/' separated, rooted-or-relative. |

## Key responsibilities

- **Contract definition** — every concrete provider in
  `Assimalign.Cohesion.FileSystem.*` implements these abstractions.
- **Factory registration** — `FileSystemFactoryBuilder` exposes three
  `AddFileSystem` overloads (pre-built instance, named factory delegate,
  typed factory delegate using `typeof(T).Name`). Provider packages add
  fluent extensions (`AddInMemoryFileSystem`, `AddPhysicalFileSystem`,
  `AddIsolatedStorageFileSystem`, `AddAggregateFileSystem`).
- **Exception mapping** — every provider raises `FileSystemException` with
  the same `FileSystemErrorCode` regardless of OS or backing technology, so
  callers can branch on the code instead of catching ad-hoc inner types.

## Source layout

```
src/
  Abstractions/          IFileSystem family + IFileSystemFactory
  Exceptions/            FileSystemException, FileSystemErrorCode
  Extensions/            FluentLogger-style extension methods
  Internal/              Implementation helpers (not exposed)
  Properties/            InternalsVisibleTo declarations
  FileSystemFactory.cs       (concrete factory, lazy creation, cascading dispose)
  FileSystemFactoryBuilder.cs (single-use builder)
tests/
  FileSystemExceptionTests.cs       (Throw* helpers + ordinal stability)
  FileSystemFactoryBuilderTests.cs  (validation, lazy materialization, Dispose)
  Shared/FileSystemStandardTests.cs (32 contract tests shared with every provider)
```

## Related

- Per-provider packages: `InMemory`, `Physical`, `IsolatedStorage`, `Aggregate`.
- Path-matching helpers: `Assimalign.Cohesion.FileSystem.Globbing`.
- Provider selection cheat-sheet: `libraries/FileSystem/README.md`.
- AOT + OS support matrix: `libraries/FileSystem/Assimalign.Cohesion.FileSystem/docs/COMPATIBILITY.md`.
