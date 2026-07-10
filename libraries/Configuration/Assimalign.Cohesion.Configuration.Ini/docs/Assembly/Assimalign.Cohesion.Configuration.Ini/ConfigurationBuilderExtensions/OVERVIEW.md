# `Assimalign.Cohesion.Configuration.Ini.ConfigurationBuilderExtensions`

Adds INI configuration provider registration helpers to `IConfigurationBuilder`.

## Methods

### `AddIniFile(IConfigurationBuilder, IFileSystem, FileSystemPath)`

Registers a required INI file at `path` resolved against `fileSystem`. No
reload-on-change. Throws `ArgumentNullException` for null arguments and
`ArgumentException` for an empty `path`.

### `AddIniFile(IConfigurationBuilder, IFileSystem, FileSystemPath, bool optional)`

Adds the `optional` flag. When `optional == true`, a missing file is silently
ignored (the provider loads zero entries). When `optional == false`, a missing
file surfaces as a `FileNotFoundException` at `Build()` time.

### `AddIniFile(IConfigurationBuilder, IFileSystem, FileSystemPath, bool optional, bool reloadOnChange)`

Full file-backed behavior. When `reloadOnChange == true`, the provider watches
the file through `IFileSystem.Watch(...)` and re-runs the parser on Create,
Change, Delete, or Rename. Reload is debounced by
`ConfigurationIniOptions.ReloadDelay` (default 250 ms).

### `AddIniFile(IConfigurationBuilder, Action<ConfigurationIniOptions>)`

Options-callback form. Lets callers configure every option, including
`OnLoadException`, `ReloadDelay`, and any future option additions.

### `AddIniStream(IConfigurationBuilder, Stream, bool leaveOpen = false)`

Registers a stream-backed provider. The stream is read in full each time the
provider loads (it is rewound if `Stream.CanSeek`). When `leaveOpen == false`,
the stream is disposed when the provider is disposed.

## Exceptions

| Exception | Condition |
| --- | --- |
| `ArgumentNullException` | Any of `builder`, `fileSystem`, `stream`, or `configureOptions` is null. |
| `ArgumentException` | The `path` is empty. |
| `FileNotFoundException` | A required (`optional == false`) file is missing at `Build()` time. |
| `FormatException` | The INI content violates the documented grammar (see `docs/DESIGN.md`). |

## Usage

```csharp
IConfiguration configuration = new ConfigurationBuilder()
    .AddIniFile(fileSystem, "settings.ini", optional: false, reloadOnChange: true)
    .Build();
```
