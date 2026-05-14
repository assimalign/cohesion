# Assimalign.Cohesion.FileSystem.IsolatedStorage

`IFileSystem` implementation backed by `System.IO.IsolatedStorage.IsolatedStorageFile`.
Provides per-user / per-assembly persistent storage without requiring the
caller to choose a directory on disk — the runtime picks an OS-appropriate
location.

```csharp
using Assimalign.Cohesion.FileSystem;

using var factory = new FileSystemFactoryBuilder()
    .AddIsolatedStorageFileSystem(options =>
    {
        options.WatchPollInterval = TimeSpan.FromSeconds(5);
    })
    .Build();

IFileSystem fs = factory.Create("IsolatedStorageFileSystem");
fs.CreateFile("userprefs.json");
```

- Cross-platform — Windows, Linux, macOS.
- Polling-based watch (configurable cadence; set to
  `Timeout.InfiniteTimeSpan` to disable).
- `Attributes` / `SetAttributes` throw `NotSupportedException` (the
  underlying store does not expose `FileAttributes`).
- `OnRename` registrations accepted but never fire (polling can't
  reliably correlate the delete+create pair).

See `docs/OVERVIEW.md` and `docs/DESIGN.md` for details.
