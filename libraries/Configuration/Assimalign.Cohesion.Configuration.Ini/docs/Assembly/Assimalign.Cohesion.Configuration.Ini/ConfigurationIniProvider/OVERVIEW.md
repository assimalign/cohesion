# `Assimalign.Cohesion.Configuration.Ini.ConfigurationIniProvider`

File-backed INI configuration provider. Extends
`Assimalign.Cohesion.Configuration.FileSystem.FileSystemConfigurationProvider`,
inheriting all file watching, optional-file handling, reload debounce, and
load-exception flow.

## Constructor

```csharp
public ConfigurationIniProvider(ConfigurationIniOptions options)
```

`options` must specify a non-null `FileSystem` and a non-empty `Path`. Throws
`ArgumentNullException` or `ArgumentException` (from the base class)
respectively.

## Members

| Member | Description |
| --- | --- |
| `Name` (override) | Stable provider name in the form `ConfigurationIniProvider[<path>]`. Used in diagnostics. |
| `ReadAsync(Stream, IDictionary<Path, string?>, CancellationToken)` (override) | Delegates parsing to `IniConfigurationParser.ParseAsync`. Called by the base class with a freshly opened read stream every time a load is triggered. |

## Lifecycle

The provider is normally created indirectly via
`ConfigurationBuilderExtensions.AddIniFile(...)`. The base class handles:

- Watching the configured `Path` for changes (if `ReloadOnChange`).
- Debouncing reload (`ReloadDelay`, default 250 ms).
- Invoking `OnLoadException` for any exception thrown during parsing.
- Suppressing `FileNotFoundException` when `Optional == true`.

## Exceptions

| Exception | Condition |
| --- | --- |
| `ArgumentNullException` | `options` is null or `options.FileSystem` is null. |
| `ArgumentException` | `options.Path` is empty. |
| `FileNotFoundException` | Required file is missing on load (suppressed via `Optional`). |
| `FormatException` | INI content violates the documented grammar. Routed through `OnLoadException` if configured. |
