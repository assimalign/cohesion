# Assimalign.Cohesion.Configuration.Ini - Design

## Goals

1. Provide a small, deterministic INI provider that fits the Cohesion
   configuration model without bringing in `Microsoft.Extensions.*`.
2. Compose with `Assimalign.Cohesion.Configuration.FileSystem` for file-backed
   behavior (`Optional`, `ReloadOnChange`, `OnLoadException`) so the provider
   does not reimplement file watching, debounce, or load-exception flow.
3. Stay AOT-safe and trim-safe: no reflection, no runtime code generation, no
   `Microsoft.Extensions.*` ambient services.

## Cohesion-supported INI grammar

INI has no authoritative external standard. This document defines the grammar
that `IniConfigurationParser` accepts; `tests/IniCompatibilityTests.cs` is the
golden corpus that pins behavior to this contract.

### Lines

A file is a sequence of newline-terminated lines. After trimming leading and
trailing whitespace, each line is classified as:

| Classification | Pattern | Effect |
| --- | --- | --- |
| Blank | `^\s*$` | Ignored. |
| Comment | `^\s*[;#]` | Ignored. |
| Section header | `^\s*\[.*\]\s*$` | Sets the active section path. |
| Assignment | `^\s*[^=\s][^=]*=.*$` | Adds an entry to the current section. |

Any line that doesn't fit one of those four classifications is a parse error
and results in a `FormatException` whose message includes the offending line
number.

### Sections

A section header is the substring inside the brackets, with leading and
trailing whitespace trimmed. The body must contain at least one non-whitespace
character; `[]` and `[   ]` both parse-fail.

The header is split on `:` to produce one or more **segments**, each of which
becomes a `Key` in the resulting `Path`. Each segment is itself trimmed, so
`[ A : B ]` becomes `Path("A", "B")`. An empty segment (e.g. `[A::B]`) is a
parse error.

A section header is in effect until the next section header or end-of-file.
Sectionless root keys are allowed by simply omitting any section header before
the assignment.

### Assignments

The **first** `=` character on the line is the delimiter. The substring before
is the **key**; the substring after is the **value**. Both are trimmed, then:

- The key contributes one more `Key` segment to the path. Empty keys
  (`= ValueOnly`) are a parse error.
- The value is taken literally, with one exception: a matched pair of
  surrounding quotes (`"..."` or `'...'`) is stripped. Inside the quotes the
  text is taken verbatim - no escape interpretation.

Duplicate paths within the same provider resolve **last-value-wins**. This
matches the behavior of the JSON provider's per-provider duplicate handling and
is consistent with how the broader configuration model layers providers for
precedence.

### Path projection

The full path for an assignment is the current section's segments followed by
the assignment key, in order. Examples:

| INI line(s) | Resulting `Path` | Value |
| --- | --- | --- |
| `Mode = Live` (no section) | `Mode` | `Live` |
| `[Logging]` / `Level = Debug` | `Logging:Level` | `Debug` |
| `[Logging:Console]` / `Level = Information` | `Logging:Console:Level` | `Information` |
| `[A]` / `K1 = X` / `K2 = Y` / `[B]` / `K1 = Z` | `A:K1=X`, `A:K2=Y`, `B:K1=Z` | - |

### Non-goals

- **Multi-line continuations.** A backslash at end-of-line is NOT interpreted
  as a continuation. The full value must fit on a single line.
- **Escape sequences inside values.** `\n`, `\t`, `\"` etc. are taken literally.
- **In-line comments after a value.** Once an assignment starts, the rest of
  the line is value text. `key = value ; comment` parses with the value
  `value ; comment`.
- **INI dialect compatibility (Windows, Python `configparser`, etc.).** The
  grammar is intentionally a Cohesion-specific dialect, not a target for
  arbitrary external INI files.

## Implementation notes

### Parser

`IniConfigurationParser` is a single static `ParseAsync` method that reads
lines from a `StreamReader` and dispatches by line classification. The
implementation avoids LINQ, regex, and reflection - the hot path is character
spans and a small list of section segments.

The reader is constructed with `detectEncodingFromByteOrderMarks: true` so
UTF-8 BOM-prefixed files (common on Windows) parse correctly, and with
`leaveOpen: true` so the lifetime of the underlying stream stays with the
caller.

### Provider lifecycle

- `ConfigurationIniProvider` extends `FileSystemConfigurationProvider` and
  implements only `ReadAsync(Stream, IDictionary<Path, string?>, CancellationToken)`.
  All file watching, debounce, optional-file handling, and exception callbacks
  are inherited.
- `ConfigurationIniStreamProvider` extends `ConfigurationProvider` directly
  because there is no file system involved. It rewinds the stream if seekable
  before each load so reload semantics work for callers that opt in via
  multi-load patterns. Disposal honors the `leaveOpen` flag.

### Registration helpers

`ConfigurationBuilderExtensions` provides three `AddIniFile` overloads (no
options, `optional`-only, full file behavior) plus an `Action<TOptions>` form,
mirroring the JSON provider. The options form composes with
`AddFileSystemProvider` in `Assimalign.Cohesion.Configuration.FileSystem`.

`AddIniStream` is a single-method extension that wraps the stream in a
`ConfigurationIniStreamProvider`. It accepts a `leaveOpen` flag because callers
sometimes want to reuse the underlying stream (e.g. multiple provider passes
over the same in-memory buffer).

## Test surface

| Suite | File | Purpose |
| --- | --- | --- |
| Parser unit tests | `IniConfigurationParserTests.cs` | Drive the parser directly through reflection. Covers every grammar rule, every error path, BOM handling, null arguments. |
| Provider integration tests | `ConfigurationIniProviderTests.cs` | Drive the public `AddIniFile` / `AddIniStream` surface end-to-end through `ConfigurationBuilder`. Validates file lifecycle (optional, missing, exception callback), stream disposal, rewind behavior, argument validation. |
| Compatibility corpus | `IniCompatibilityTests.cs` | Golden table of (INI fragment -> expected `Path` and value entries). Pins the grammar contract; changing parser behavior requires updating both this corpus and `DESIGN.md`. |

Coverage target: >80% line and branch. Current coverage: 100% line, 97.7% branch.

## Known constraints

- The parser materializes one `string` per value and one per key segment. For
  multi-megabyte INI files this is unavoidable given the configuration model
  shape (`IDictionary<Path, string?>`).
- File-backed lifecycle depends entirely on `IFileSystem.Watch(...)`. Providers
  whose backing file system can't watch will silently not reload; that is a
  limitation of the underlying file system implementation, not the INI
  provider.
