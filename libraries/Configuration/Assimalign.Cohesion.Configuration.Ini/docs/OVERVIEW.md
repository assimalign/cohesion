# Assimalign.Cohesion.Configuration.Ini

INI-format configuration provider for the Cohesion configuration stack. Loads
INI content from streams or from a Cohesion `IFileSystem`-backed file into the
shared `IConfiguration` shape used by every Cohesion configuration provider.

## Scope

- INI **source loading** for stream and file scenarios.
- INI **path projection** onto Cohesion `Path` semantics so callers index loaded
  entries with the same `Section:SubSection:Key` style they use for JSON, XML,
  command-line, and environment-variable providers.
- File-backed lifecycle behavior (`Optional`, `ReloadOnChange`, `ReloadDelay`,
  `OnLoadException`) is delegated to `Assimalign.Cohesion.Configuration.FileSystem`
  so file watching and exception handling stay consistent across providers.

Non-goals - explicitly out of scope:

- INI writing or serialization.
- Multi-line value continuations (`\` line endings, indented continuations).
- Escape-sequence interpretation inside values (e.g. `\n`, `\t`).
- An "officially compatible" INI dialect. The grammar is defined locally in
  `docs/DESIGN.md` and pinned by a golden-corpus compatibility test suite. See
  the Standards and Compliance section below.

## Dependencies

- `Assimalign.Cohesion.Configuration` for the configuration model and provider
  base types.
- `Assimalign.Cohesion.Configuration.FileSystem` for the shared file-backed
  provider lifecycle (`Optional`, `ReloadOnChange`, etc).

No dependency on `Microsoft.Extensions.*`.

## Quick start

```csharp
using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Configuration.Ini;
using Assimalign.Cohesion.FileSystem;

// File-backed (with reload-on-change).
IConfiguration configuration = new ConfigurationBuilder()
    .AddIniFile(fileSystem, "settings.ini", optional: false, reloadOnChange: true)
    .Build();

string? level = configuration["Logging:Console:Level"];
```

```csharp
// Stream-backed (e.g. embedded resource or remote download).
using Stream stream = await DownloadAsync(cancellationToken);

IConfiguration configuration = new ConfigurationBuilder()
    .AddIniStream(stream, leaveOpen: false)
    .Build();
```

## Supported INI grammar (summary)

See `docs/DESIGN.md` for the authoritative grammar and the rationale behind each
rule. Headline points:

- **Section headers** use square brackets: `[Section]`. Section names may
  contain `:` to nest deeper, so `[Logging:Console]` plus `Level = Debug`
  produces the key `Logging:Console:Level`.
- **Sectionless root keys** are supported: `Mode = Live` before any section
  produces the key `Mode`.
- **Comments** start with `;` or `#` and run to end-of-line. They must be the
  first non-whitespace character on the line.
- **Whitespace** around the section name, key, and either side of `=` is
  trimmed. Whitespace inside a value is preserved.
- **The first `=` is the delimiter.** Subsequent `=` characters belong to the
  value, so `ConnectionString = Server=db;User=app` works as expected.
- **Matched surrounding quotes** (`"..."` or `'...'`) are stripped from the
  value but the inner text is taken literally - no escape sequences.
- **Duplicate keys** resolve last-value-wins within the same provider.

## NativeAOT and trimming

- The parser uses `StreamReader` and a hand-rolled state machine, with no
  reflection or runtime code generation. It is AOT-safe and trim-safe.
- File-backed lifecycle inherits the AOT compatibility of
  `Assimalign.Cohesion.Configuration.FileSystem`.

## Standards and compliance

INI has no single authoritative IETF or consortium specification. The Cohesion
INI grammar is defined locally in `docs/DESIGN.md` and pinned by a golden
compatibility corpus in `tests/IniCompatibilityTests.cs`. Future parser changes
must update both the design doc and the corpus together.
