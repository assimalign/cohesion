# Assimalign.Cohesion.Core Design

## Design Intent

This package is deliberately broad but lightweight. It collects reusable system-level building blocks so the higher-level packages can share a common vocabulary without depending on each other.

## Implementation Note

Examples below document the intended public shape; some members still throw NotImplementedException today.

## Architecture

- Core value types such as Size and Glob capture reusable concepts that show up across multiple libraries.
- Exception and system extension helpers centralize small cross-cutting behaviors.
- The library is dependency-light by design so other Core packages can reference it safely.

## Component integration callbacks

`ComponentIntegrationAttribute` and its catalog are internal source in Core's
`shared/ComponentIntegration` module. Core's runtime assembly does not compile or publicly expose
these types. A contributing project opts in with
`<CohesionSharedSource Include="Assimalign.Cohesion.Core" Module="ComponentIntegration" />`, obtaining
an assembly-local internal copy without a DI reference. The root source generator recognizes those
copies from assembly metadata, including when the consumer has no visible attribute type.

Schema version 2 replaces open target/member strings with the closed `ComponentIntegrationTarget`
and `ComponentIntegrationMethod` enums. Each declaration selects a `ComponentIntegrationTemplate`:
`Factory`, `Builder`, `BuilderWithServiceProvider`, or `Custom`. The two builder signatures require
separate declarations. Bare and untyped-null calls retain eager construction; provider callbacks
construct inside the target's producer. `Custom` accepts a signature and complete body with documented
placeholders. This intentionally removes the old public Core attribute API; every repository
contributor opts into the new shared contract. See the
[generator design](../../../../analyzers/Assimalign.Cohesion.SourceGeneration/docs/DESIGN.md).

## Generated value-type conformance

Core's tests compile `CohesionValueType` fixtures for every supported runtime type and exercise
comparison, validation, class and struct kinds, parsing, formatting and source-generated JSON
metadata. `GeneratedValueTypeTests` covers this contract; `GeneratedValueTypeRegressionTests`
keeps runtime regressions on the earlier generated surface so the same tests can run against the
baseline generator. Compilation fixtures pin all accessibility modes (Public, Internal, None and
omitted), including public handwritten partials for None and omitted metadata. Both None and an
omitted `ObjectAccessModifier` emit no accessibility modifier, leaving the handwritten partial to
specify it. Invalid-metadata task tests assert each `COHVT001` through `COHVT006` diagnostic, the
item name and the metadata name, and verify that failure produces no generated output. Public
test method signatures assert public fixture accessibility at compile time, preserving existing
behavior without runtime reflection. The conformance classes share a non-parallel collection
because they temporarily change the current
culture and count validation calls. Every culture change is restored in `finally`.

`ObjectHasCustomToString="true"` lets a handwritten partial own its parameterless `ToString()`
and `DebuggerDisplay`. `GeneratedRedactedString` supplies both, so compiling the fixture catches
duplicate generated members or attributes without reflection. Runtime tests cover redaction for
populated and default values while preserving validation, equality and source-generated JSON.
Direct task tests cover struct/class and string/numeric generation, the unchanged omitted/false
default, and `COHVT005` for invalid boolean metadata. The option changes diagnostic display only;
explicit `Value` access and JSON still carry the underlying value. It is additive and opt-in
relative to 10.0.0-preview.1, with no behavior change for existing metadata.

The build task remains outside the shipped Core assembly. See the
[generator contract](../../../../docs/build/MSBUILD_COHESION_PROPS.md#value-types) for metadata,
default-value semantics and preview compatibility changes. `GeneratedValueTypeJsonContext` is an
ordinary source-generated context outside the value-object partials. Each value object declares
one nested `public sealed partial class XJsonConverter : JsonConverter<X>` and its
`JsonConverter` attribute references that converter directly. The converter and its members have
XML documentation, and its public parameterless constructor lets System.Text.Json instantiate it.
A context in another assembly can use a public value object's converter without supplying a
custom converter or using reflection-based serialization. Http.Cookies' separate test assembly
pins this boundary with a model containing `HttpCookieName`: its source-generated context round
trips the value as a JSON string, and SYSLIB1220 and SYSLIB1030 are errors in that test project.
JSON null handling, invalid-value exceptions and dictionary-key conversion are unchanged.

The public nested converters add public surface compared with 10.0.0-preview.1's non-public
converters. On regeneration, handwritten converter partials that declare `internal` or `private`
conflict with the new public declaration (CS0262). Remove redundant partial declarations, as
ApplicationModel does, or omit their accessibility modifier if they supply additional members.

`System.Ulid` owns validation for every string, character-span and UTF-8 `Parse` and `TryParse`
overload, including those with a nullable format provider. Input must have exactly 26 characters
from Crockford's base32 alphabet `0123456789ABCDEFGHJKMNPQRSTVWXYZ`, case-insensitively; `I`, `L`,
`O` and `U` are invalid. The first character must be `0` through `7` to fit the 128-bit value.
`TryParse` returns false for invalid text and `Parse` throws `FormatException`. Valid lowercase
input is accepted, and byte values and canonical uppercase encoding are unchanged.

Generated ULID JSON converters delegate to `Ulid.TryParse`, so scalar values, dictionary keys and
direct parsing share the same rule. Compared with 10.0.0-preview.1, malformed and overflowing text
that previously decoded silently is now rejected by `Ulid.Parse` and `Ulid.TryParse`, including
through generated wrapper parsers. Generated JSON reads reject the same text with `JsonException`.
Wrong-length `Ulid.Parse` input now throws `FormatException` instead of `ArgumentException`.
This is an intentional behavioral breaking change: malformed spellings can no longer alias a
valid identifier or overwrite a dictionary entry under that identifier.

## Adaptive memory-pool retention

`AdaptiveMemoryPoolPressurePolicy.PeakRetentionRatio` accepts only finite doubles in `[0, 1]`.
The setter rejects invalid input with `ArgumentOutOfRangeException` before changing the policy,
so NaN cannot reach the peak-count multiplication, ceiling, or integer conversion.
Zero and one remain valid. Compared with 10.0.0-preview.1, rejecting NaN on assignment is an
intentional behavioral breaking change (#1490); the public member signatures are unchanged.

## Frozen runtime contract

`AppEnvironment.Variables` is the only .NET home of the version 1 gateway-to-resource contract
documented in `docs/RUNTIME_CONTRACT.md`. Its fixed names and parameterized patterns are
`public const string` values. Constants are inlined into consuming assemblies, so changing any
value is a breaking wire-contract change rather than an ordinary implementation change. The Core
runtime-contract guard tests pin `AppEnvironment.Variables.cs`: its public string constants must
equal the document's variable table exactly, in order, and no line containing `COHESION_` (code,
string literal, or comment) may appear in the `src/` C# files of `libraries/Core`,
`libraries/Hosting`, or `libraries/ApplicationModel` outside that file; XML docs name a
variable with `<see cref="AppEnvironment.Variables.X"/>` instead.
Future `AppEnvironment` work must leave `Variables` where it is and as it is.

The contract lives on `AppEnvironment` because that type was always meant to hold it: it has been
Cohesion's process-environment reader since the initial push, `ResourceEnvironment` was created
beside it in `a60c3c4a` (runtime-contract item L01.01.10.03), and owner decision R9 (2026-09-26)
folded it back, moving its readers onto `AppEnvironment` and its constants and builders onto
`AppEnvironment.Variables`. `AppEnvironment.Keys.EnvironmentKey` is `Variables.Environment`, so
the environment-name rule and the contract share one literal.

The `Variables` name builders (`Endpoint`, `Dependency`, `Mount`) apply one ordinal ASCII
normalization rule to endpoint, resource, and mount components: lowercase ASCII is uppercased,
ASCII letters and digits are retained, and each other character becomes one underscore.
`Configuration` inserts its section and key verbatim. `AppEnvironment`'s readers
(`TryGetEndpoint`, `TryGetDependency`, `TryGetMount`) compose every name they read through those
builders, and the gateways write names with the same builders, so the two sides cannot normalize
differently. Typed endpoint readers return `System.Uri`: the BCL type already owns scheme, host,
port, path, parsing, and equality, so Core does not duplicate that state in a framework-specific
address type. A declared endpoint's HOST/PORT/SCHEME values describe its bind address; its
separately advertised PUBLIC_URL remains available through the URI reader. Dependency URLs may
carry an optional path.

The default application-key credential profile constants are not in Core. They are
`Assimalign.Cohesion.Hosting.Resources.ResourceCredentialProfile`, beside the resource credential
verifier seam, because every gateway and hosting module that mints or verifies the default
credential already references Hosting.Resources; the base ApplicationModel and the platform
gateways never use them.

`UriExtensions` is a single C# 14 extension block in the `System` namespace for the endpoint
behavior the BCL does not supply directly: `CreateEndpoint`, `TryCreateEndpoint`,
`TryParseEndpoint`, `ThrowIfNotEndpoint`, `IsEndpoint`, `EndpointPath`, and
`ToEndpointString()`. An endpoint is absolute, has a host and an explicit port from 1 through
65535, and has no user information, query, or fragment. `ToEndpointString()` is the sole writer
for runtime-contract URL values: `scheme://host:port[/path]`, with an escaped path and no trailing
slash for the root. Code crossing into socket APIs uses `Uri.IdnHost` by convention so IPv6
literals are unbracketed and internationalized hosts use their ASCII-compatible form.

`AppEnvironment` owns the single environment-name rule:
`COHESION_ENVIRONMENT ?? DOTNET_ENVIRONMENT ?? "Production"`. ApplicationModel delegates to this
Core rule rather than parsing process variables itself. Every `AppEnvironment` reader has a
process-environment and a dictionary overload so the ambient
`Assimalign.Cohesion.Hosting.Resources.ResourceContext` can carry the same keys without duplicating
their parsing rules.

`ResourceContext` and `ResourceRuntime` live in `Assimalign.Cohesion.Hosting.Resources`, not
Core; they are the in-process carrier delivered by runtime-contract item 12.

## `FileSystemPath.Merge` navigates; it does not confine

`FileSystemPath` (namespace `System.IO`) is the path type of the FileSystem family. Its `Merge`
joins a relative path onto a base, returns a path that already lies under the base, and applies
leading `..` segments, which may climb above the base. It is not a containment check, and the
FileSystem providers do not use it as one: each resolves incoming paths and refuses anything
outside its root itself (`libraries/FileSystem/Assimalign.Cohesion.FileSystem/docs/DESIGN.md`,
"Root containment", which records why the check is not a `FileSystemPath` API).

Its rules since #1180: the prefix match is ordinal (ignore-case when asked) and needs a segment
boundary, so `/srv/public2` is not under `/srv/public`; only an exact `..` segment is a parent
reference; `..` stops at the base's root — the drive, leading `/`, or UNC share — and throws
there instead of producing a relative or `//`-prefixed result; and the `CultureInfo` parameter no
longer takes part in the match, because a culture-aware match cannot decide a segment boundary.

## FileSystemPath and Win32 device paths

`FileSystemPath.Parse` normalizes a Win32 extended-length (`\\?\`) or device (`\\.\`) prefix
away when the prefix names a drive or a UNC share, rather than preserving it:

| Input | Parsed value |
| --- | --- |
| `\\?\C:\a\b`, `\\.\C:\a\b` | `C:/a/b` |
| `\\?\UNC\server\share\a`, `\\.\UNC\server\share\a` | `//server/share/a` |
| `\\?\Volume{guid}\a`, `\\.\pipe\name`, `\\?\GLOBALROOT\...` | `ArgumentException` |

Why it matters: the .NET host fully qualifies an application path at or past `MAX_PATH` (260)
with `\\?\`, so `AppContext.BaseDirectory`, and `ResourceContext.ContentRootPath` derived from
it, arrive prefixed. Every area hosting module parses one of them at startup, so rejecting the
`?` stopped every Cohesion application installed under a deep directory.

Why normalize rather than preserve:

- **One spelling per location.** `FileSystemPath` equality, hashing, and
  `FileSystemPathComparer` are plain string comparisons, so `\\?\C:\a` and `C:\a` would otherwise
  be two different keys for one directory. `HasDrive`, `HasShare`, `HasRoot`, `GetSegments`,
  `Join`, and `Merge` also keep working unchanged on the normalized form.
- **Nothing is lost for long paths.** .NET file APIs add `\\?\` themselves to a path at or past
  `MAX_PATH` after normalizing it, so `PhysicalFileSystem` opens a normalized `C:/...` path past
  260 characters (covered by a Physical test and checked by hand through `dotnet <app>.dll` from a
  279-character directory).

Known costs: `\\?\` also suppresses Win32 normalization, so a component that only that form can
name, such as a trailing dot or space, loses that meaning once the prefix is stripped.
`FileSystemPath` treats both separators alike on every platform, so the same normalization
applies off Windows. A device target that is neither a drive nor `UNC\` has no ordinary spelling,
so it is rejected rather than silently turned into a share named `.` or `?`. `?` stays illegal
everywhere except the prefix, and the NT `\??\` prefix is still rejected. Error indexes refer to
the caller's original string.

## `Glob.Parse` is thread-safe

`Glob.Parse`, and the implicit conversion from `string` that calls it, may run on any number of
threads at once. A parsed `Glob` never changes, so one instance can be shared across threads.

- **Tokenizer state belongs to one parse.** Each cache miss creates its own `Glob.Parser`, which
  owns the `StringBuilder` that collects literal, set and range text. Every parse used to go
  through one static parser and share that buffer. Two parses at once spliced each other's text,
  so `/file[0-9].txt` could come back as a range such as `[0-x]` that matches `/filea.txt`, or the
  parse threw `IndexOutOfRangeException`. That was the intermittent FileSystem.Globbing failure
  on PR #1168 (run 38792058025).
- **The pattern cache is process-wide and concurrent.** Token arrays are cached in a
  `ConcurrentDictionary<string, TokenBase[]>` with ordinal keys. A lookup takes no lock and
  allocates nothing, so parsing a cached pattern allocates only the returned `Glob`. When first
  parses of one pattern race, each tokenizes it and `GetOrAdd` gives every caller the array that
  was stored. The old cache was a `Dictionary` that `Tokenize` read but never wrote, so no parse
  had ever been served from it.
- **The cache is bounded by clearing it.** It is cleared after every 1,024 misses. Patterns come
  from callers, and the FileSystem providers' `Watch` parses each watched path as a pattern, so a
  cache that kept every distinct pattern would grow for the life of the process. Clearing keeps the
  recent working set without the per-hit bookkeeping an LRU needs; a cleared pattern is parsed
  again on its next use.
- **Globs parsed from one pattern share a token array, and `Glob.Tokens` cannot change it.**
  `Tokens` returns an `ImmutableArray<Token>` that wraps the shared array without copying it
  (`ImmutableArray<Token>.CastUp` over `ImmutableCollectionsMarshal.AsImmutableArray`), so a read
  allocates nothing and a cached parse still allocates only the `Glob`. It used to return the
  `Token[]` itself, so one caller's write changed every glob of that pattern until the cache next
  cleared. A copy was rejected because it allocates on every read, the case CA1819 warns about.
  `ReadOnlySpan<Token>` allocates nothing either, but callers cannot store it, capture it in a
  lambda, hold it across `await` or query it with LINQ. An `IReadOnlyList<Token>` wrapper would
  add a field to every `Glob` and allocate on first read. Only marshalling APIs such as
  `ImmutableCollectionsMarshal.AsArray`, and reflection, still reach the array.

`GlobConcurrencyTests` pins both halves. Workers parse distinct range, set, brace and wildcard
patterns at once, re-parse earlier ones while the cache fills and clears, and check each glob's
matches and `ToString()` round trip. The run ends once 2,000 parses have overlapped another parse,
not after a fixed time, and fails if that never happens. A second test asserts that parsing a
cached pattern allocates exactly one `Glob`. `GlobTokensTests` asserts that writes through
`Tokens` throw `NotSupportedException` and leave the shared array intact, and that reading the
tokens allocates nothing.

## `Glob` brace groups match the whole input

The parse cache stores an internal `Glob.GlobPattern`, nested in `Glob`, containing the immutable
tokens, minimum input length and whether it consumes variable-length input. Matching reuses those values instead of
walking every token before each candidate. This moves the length precheck from O(tokens) to
O(1) per match. One metadata object is retained per cached pattern; a cached parse still allocates
only one `Glob` of the same size. Token matching and brace-group semantics are unchanged.

Three defects in brace groups, recorded in #1507, are fixed together:

- **A match could stop one character short.** The end-of-input checks in `Glob.IsMatch` and
  `CompositeGlobToken.TestAll` tested `position < value.Length - 1`, so `**/*.{js,ts}` matched
  `src/App.tsx` and `a{b,c}d` matched `abdx`. Without a group, the up-front fixed-length check, or
  a `*` that anchors fixed-length tokens to the end, caught the extra character. A group is
  variable-length, which turns both off. The checks now require the match to reach the end.
- **Tokens after a longer alternative ran out of input.** The length checks count only a group's
  shortest alternative, so `{ab,c}/` passes them on `ab`. A `*` or `**` owns the rest of the
  pattern, so the group is the one variable-length token that other tokens follow. A path
  separator, `?`, set or range token after it then read past the end, and `IsMatch` threw
  `IndexOutOfRangeException`. Each now returns `false` at the end. A `*` followed by fixed-length
  tokens had the same blind spot without throwing: it placed those tokens at the end of the input,
  which is before the `*` itself once the group took the longer alternative, so `{ab,c}*b` matched
  `ab` by reading the group's `b` twice. That `*` now fails when too little input is left.
- **An empty group threw `InvalidOperationException`.** `{}`, `{,}` (empty alternatives are
  dropped) and a `{` that ends the pattern leave a group with no alternatives, and LINQ's `Min`
  over them threw. `Glob.Parse` now throws `ArgumentException` for `pattern`, with the
  `GlobBraceGroupEmpty` message giving the group's index. A pattern that fails to parse is not
  cached.

One more change keeps the fix inside brace groups. A `*` whose remaining tokens can match nothing,
such as the `/**` in `src*/**`, falls back to taking the rest of its segment, but it reported the
position of its last failed attempt rather than the end. The old check let that through. The
corrected one would have rejected `srcx` while still accepting `src`. The fallback now reports the
end, so every pattern without a brace group matches as before. This change leaves open whether
`src*/**` should match `srcx` at all, given that `src/**` does not match `src`.

Within the documented limits, a brace group now behaves as the atomic regex group `(?>md|cs)`:
the first alternative that matches at its position is kept, and the rest of the pattern must match
what follows it. Compared with 10.0.0-preview.1, the corrected results are an intentional
behavioral breaking change. `IsMatch` returns `false` where it returned `true` or threw, and
`Glob.Parse` throws `ArgumentException` instead of `InvalidOperationException`. The public member
signatures are unchanged, and so are the limits the overview lists: alternatives are tried in
order without backtracking, and empty alternatives and wildcards inside alternatives are not
supported. `GlobBraceGroupingTests` pins each case.

## AOT posture

Runtime-contract parsing uses direct string, integer, and URI operations. It performs no
reflection, assembly scanning, dynamic code generation, or runtime serialization.

## Layout Example

```text
Assimalign.Cohesion.Core/
  src/
    Assimalign.Cohesion.Core.csproj
    Exceptions/
    Internal/
    Properties/
    Shared/
    System/
    Utilities/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Work with reusable size primitives

```csharp
Size payload = Size.FromKilobytes(512);

Console.WriteLine(payload.ToString("ki"));
Console.WriteLine(payload.Megabytes);
```

## Example 2: Use shared environment and glob helpers

```csharp
Glob pattern = Glob.Parse("**/*.json");
bool isMatch = pattern.IsMatch("settings/appsettings.json");

string environmentName = AppEnvironment.GetEnvironmentName();
```

`AppEnvironment.Variables.TrustBundlePath` is the additive transport trust-anchor path immediately after the bootstrap path. Its contents are certificates only; Hosting.Resources owns protected-file reading and TLS chain validation. The constant, runtime table, and golden variable list remain in identical order.

## Build-generated resources (#1467)

Exception messages are held in `src/Properties/ErrorMessages.resx`. The internal `ErrorMessages` accessor is generated in the project
root namespace by MSBuild's GenerateResource task through
`build/Targets/Build.ErrorMessages.targets`. The generated source stays under
`obj/`; no checked-in designer or Visual Studio custom tool is required. Ordinary
`dotnet build`, CI and NativeAOT publication use the same embedded resources.
Existing message text is preserved. Resource names describe failures in short PascalCase
noun phrases of at most 40 characters (#1471); callers use the renamed internal accessors.
