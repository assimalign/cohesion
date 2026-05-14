# FileSystem Provider Compatibility Matrix

This document tracks the runtime, OS, and NativeAOT compatibility of each
`IFileSystem` provider in the family. It is the source of truth referenced
by `libraries/FileSystem/README.md`.

## Target framework

Every package targets the central `TargetFrameworkForLibraries` defined in
`build/Targets/Build.TargetFramework.props` — currently **net10.0**.

## OS support

| Provider | Linux | Windows | macOS | Notes |
|----------|:-----:|:-------:|:-----:|-------|
| `Assimalign.Cohesion.FileSystem` | ✅ | ✅ | ✅ | Contract-only; no platform dependency. |
| `…InMemory` | ✅ | ✅ | ✅ | Pure managed; no OS dependency. |
| `…Physical` | ✅ | ✅ | ✅ | Uses `System.IO` + `DriveInfo` + `FileSystemWatcher`. Watch latency is OS-dependent (inotify / ReadDirectoryChangesW / kqueue). |
| `…IsolatedStorage` | ✅ | ✅ | ✅ | Uses `System.IO.IsolatedStorage` which is cross-platform in modern .NET. Store location varies by OS (`%USERPROFILE%\AppData\IsolatedStorage` on Windows, `~/.local/share/IsolatedStorage` on Linux, etc.). |
| `…Aggregate` | ✅ | ✅ | ✅ | Composition only; inherits the OS support of the providers it mounts. |

CI runs every provider on `ubuntu-latest`, `windows-latest`, and
`macos-latest` (`.github/workflows/library-filesystem.yml`).

## NativeAOT compatibility

`libraries/Directory.Build.props` sets `<IsAotCompatible>true</IsAotCompatible>`
for every project in this directory, so the AOT analyzer runs at build
time and surfaces IL2xxx / IL3xxx warnings inline.

| Provider | `IsAotCompatible` | Trim warnings | Dynamic-code warnings | Notes |
|----------|:-----------------:|:-------------:|:---------------------:|-------|
| `Assimalign.Cohesion.FileSystem` | ✅ | 0 | 0 | |
| `…InMemory` | ✅ | 0 | 0 | |
| `…Physical` | ✅ | 0 | 0 | `FileSystemWatcher` is AOT-compatible in modern .NET. |
| `…IsolatedStorage` | ✅ | 0 | 0 | `IsolatedStorageFile.GetStore` overloads are AOT-friendly when called with literal `Type` parameters. |
| `…Aggregate` | ✅ | 0 | 0 | Routes through `IFileSystem` only; no reflection. |

### Verification

The shared build via the standard `dotnet build` pipeline runs the AOT
analyzer. A zero-warning build is the contract — any IL2xxx / IL3xxx
warning becomes a real error under the repo's
`<TreatWarningsAsErrors>` policy (set elsewhere in the build infra).

End-to-end `dotnet publish -p:PublishAot=true` for a sample console app
that exercises every provider is tracked as a follow-up enhancement
(adds a separate smoke project under
`libraries/FileSystem/tests/Aot.Smoke/`). For now the analyzer running
during the test-project builds — which transitively reference every
provider — is the effective gate.

## Watch capability matrix

| Provider | Native events | Polling | OnCreate | OnDelete | OnChange | OnRename |
|----------|:-------------:|:-------:|:--------:|:--------:|:--------:|:--------:|
| InMemory | ✅ (in-process dispatcher) | — | ✅ | ✅ | ✅ | ✅ |
| Physical | ✅ (`FileSystemWatcher`) | — | ✅ | ✅ | ✅ | ✅ |
| IsolatedStorage | — | ✅ (configurable cadence; default 1 s) | ✅ | ✅ | ✅ | ⚠️ accepted but never fires — see DESIGN |
| Aggregate | — | — | fan-in (depends on mounted providers) | fan-in | fan-in | fan-in (depends on mounts) |

## Capability matrix

| Capability | InMemory | Physical | IsolatedStorage | Aggregate |
|------------|:--------:|:--------:|:---------------:|:---------:|
| `CreateFile` / `CreateDirectory` (auto-create parents) | ✅ | ✅ | ✅ | ✅ (delegates to mount) |
| `CopyFile` | ✅ | ✅ | ✅ | ✅ (cross-provider streams) |
| `Move` (file) | ✅ | ✅ | ✅ | ✅ (cross-provider stream + delete) |
| `Move` (directory) | ✅ | ✅ | ✅ | depends on mount |
| `Watch` | ✅ | ✅ | ✅ (polling) | ✅ (fan-in) |
| `IFileSystemInfo.Attributes` | ✅ | ✅ | ❌ `NotSupportedException` | passes through to mount |
| Quota / size limit | ✅ (`Size` option) | ❌ (uses partition free space) | ✅ (store quota) | sum of mounts |
| Read-only mode | ✅ (`IsReadOnly`) | ✅ | ✅ | ✅ |
| `IDisposable` cascading | n/a | n/a | n/a | ✅ when `ownsFileSystem: true` |

## Test counts

| Project | Contract | Provider-specific | Total |
|---------|---------:|------------------:|------:|
| `Assimalign.Cohesion.FileSystem` (root) | — | 39 | 39 |
| `…InMemory` | 34 | 75 | 109 |
| `…Physical` | 34 | 32 | 66 |
| `…IsolatedStorage` | 34 | 44 | 78 |
| `…Aggregate` | 34 | 43 | 77 |
| **Total** | 136 | 233 | **369** |

The root project's 39 tests cover the abstractions directly
(`FileSystemFactoryBuilder`, `FileSystemException` helpers) and don't
inherit the contract suite — the suite is for concrete providers only.

Contract tests live in `libraries/FileSystem/Assimalign.Cohesion.FileSystem/tests/Shared/FileSystemStandardTests.cs`
and are inherited via `<Compile Include …>` into every provider's test
project. Provider-specific tests cover the behavior unique to each
implementation (quota enforcement, OS exception mapping, polling cadence,
mount routing, etc.).
