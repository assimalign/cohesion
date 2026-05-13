# `Assimalign.Cohesion.Configuration.Ini.ConfigurationIniOptions`

Options used to configure file-backed INI providers. Inherits every option
from `Assimalign.Cohesion.Configuration.FileSystem.FileSystemConfigurationOptions`;
no INI-specific options are added today.

## Inherited properties

| Property | Type | Description |
| --- | --- | --- |
| `FileSystem` | `IFileSystem?` | The file system used to resolve `Path`. Required. |
| `Path` | `FileSystemPath` | The path to the INI file within the configured file system. Required (non-empty). |
| `Optional` | `bool` | When `true`, a missing file is silently ignored. Default `false`. |
| `ReloadOnChange` | `bool` | When `true`, the provider re-runs the parser when the file changes. Default `false`. |
| `ReloadDelay` | `TimeSpan` | Debounce delay between change notification and reload. Default 250 ms. Must be non-negative. |
| `OnLoadException` | `Action<ConfigurationFileLoadExceptionContext>?` | Callback invoked when load throws. Set `ctx.Ignore = true` to suppress propagation. |

## Usage

```csharp
new ConfigurationBuilder()
    .AddIniFile(options =>
    {
        options.FileSystem    = fileSystem;
        options.Path          = "settings.ini";
        options.Optional      = false;
        options.ReloadOnChange = true;
        options.ReloadDelay   = TimeSpan.FromSeconds(1);
        options.OnLoadException = ctx =>
        {
            logger.LogWarning(ctx.Exception, "INI load failed at {Path}.", ctx.Provider.Name);
            ctx.Ignore = true;
        };
    })
    .Build();
```
