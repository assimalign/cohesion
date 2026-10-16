
# Cohesion Custom MSBuild Items

- [Cohesion Custom MSBuild Items](#cohesion-custom-msbuild-items)
  - [Items](#items)
    - [Reference Items](#reference-items)
      - [Projects](#projects)
      - [Packages](#packages)
    - [Code Generation](#code-generation)
      - [Value Types](#value-types)
  - [Related Docs](#related-docs)

---
## Items

This document describes Cohesion-specific MSBuild items that simplify common tasks in project files. Use these items directly in a `.csproj` to take advantage of centralized behavior managed by the Cohesion build.

<br/><br/>

### Reference Items

Custom items for managing dependencies within a project.
<br/>
#### Projects
>`CohesionProjectReference` lets you reference another project by its `.csproj` name. Paths are resolved within the repository, so if the referenced project moves, the reference continues to work without changes.
<br/>
>Example: `<CohesionProjectReference Include="{Project Name}"/>`
>```xml
><ItemGroup>
 > <CohesionProjectReference Include="Assimalign.Cohesion.Core" />
 > <!-- Add more by project name as needed -->
 > <!-- <CohesionProjectReference Include="Assimalign.Cohesion.Web" /> -->
 > <!-- <CohesionProjectReference Include="Assimalign.Cohesion.Database" /> -->
 > <!-- <CohesionProjectReference Include="Assimalign.Cohesion.Logging" /> -->
 > <!-- <CohesionProjectReference Include="Assimalign.Cohesion.Hosting" /> -->
>></ItemGroup>
>```

Notes:
- Intended for projects that live inside this repository.
- Use the exact project name (without `.csproj`).

#### Packages

`CohesionPackageReference` lets you add a package by name. Package versions and common metadata are centrally managed by the Cohesion build so individual projects do not repeat version numbers.

Versions are declared centrally in [`build/Targets/Build.References.Packages.targets`](../../build/Targets/Build.References.Packages.targets); see the build-system rules in [`.claude/rules/build-system.md`](../../.claude/rules/build-system.md) for details.

Example: `<CohesionPackageReference Include="{Package Name}"/>`

```xml
<ItemGroup>
  <CohesionPackageReference Include="Newtonsoft.Json" />
  <!-- Add more packages by name as needed -->
  <!-- <CohesionPackageReference Include="Serilog" /> -->
  <!-- <CohesionPackageReference Include="Polly" /> -->
  <!-- <CohesionPackageReference Include="FluentValidation" /> -->
</ItemGroup>
```

---

### Code Generation

Items that trigger Cohesion’s MSBuild-driven code generation.

#### Value Types

`CohesionValueType` generates a partial value object around one supported runtime type. The
item's filename supplies the type name; its relative directory is preserved under the intermediate
`CodeGeneration` directory. Put handwritten partials under `ValueObjects/`. Generated sources are
build output, not checked-in source.

```xml
<ItemGroup>
  <CohesionValueType
      Include="ValueObjects\HostId.cs"
      ObjectRuntimeType="Ulid"
      ObjectNamespace="Assimalign.Cohesion.Hosting"
      ObjectAccessModifier="public"
      ObjectHasImplicitOperators="true" />
</ItemGroup>
```

Metadata:

| Name | Allowed values | Default |
| --- | --- | --- |
| `Include` | Project-relative partial source path, e.g. `ValueObjects\HostId.cs` | Required |
| `ObjectRuntimeType` | `Int`, `Short`, `Long`, `UInt`, `UShort`, `ULong`, `Double`, `Decimal`, `String`, `Guid`, `Ulid` | Required |
| `ObjectNamespace` | Non-empty C# namespace | Required |
| `ObjectKind` | `Struct`, `Class` | `Struct` |
| `ObjectAccessModifier` | `public`, `internal`, `None` | No modifier, the same as `None`; another partial may specify accessibility |
| `ObjectHasImplicitOperators` | `true`, `false` | `false` |
| `ObjectHasIsValidMethod` | `true`, `false` | `false` |
| `ObjectHasCustomToString` | `true`, `false` | `false` |
| `ObjectComparison` | `Ordinal`, `OrdinalIgnoreCase`; only valid for `String` | `Ordinal` |

Omitting `ObjectAccessModifier` or setting it to `None` emits no accessibility modifier. A
handwritten `public` partial therefore determines public visibility; without an explicit modifier
on any partial, C# uses internal visibility.

The target supplies `ObjectFilePath` and `ObjectRelativePath` for compilation and IDE links; these
are derived metadata, not configuration knobs. The generated file has an auto-generated header,
nullable annotations and XML documentation, without nullable-warning suppression.

**Generated documentation.** Every public member documents each parameter under its declared name
and the exceptions it throws or propagates. `<inheritdoc/>` is used only where the generated
parameter names match the inherited BCL contract: `Equals`, `CompareTo`, `GetHashCode`, and the
formatted `ToString` and `TryFormat`. The operators (`a`, `b`) and the provider `Parse`/`TryParse`
overloads (`value`, `span`) differ from their interfaces (`left`/`right`, `s`), so they carry
explicit tags. The compiler checks those when `GenerateDocumentationFile` is on (CS1572, CS1573),
but it never checks an `<inheritdoc/>` name against the inherited one. Exception tags cover
unsupported format strings (numeric and Guid; Ulid ignores the format), numeric overflow in
`Parse` (integral and decimal types; `double` saturates to infinity), and the `Utf8JsonWriter`
failures the converter's `Write` and `WriteAsPropertyName` propagate. Those are
`InvalidOperationException` for an invalid writer position, and `ArgumentException` for an
oversized string or a NaN or infinite `double`.

**Generated contract.** Structs are `readonly partial struct`; classes are `sealed partial class`.
Both expose a constructor, a get-only `Value`, `IEquatable<T>`, `IComparable<T>`,
`IEqualityOperators<T,T,bool>`, `IComparisonOperators<T,T,bool>`, equality and comparison operators,
`Equals(object?)`, `GetHashCode()`, and a non-null `ToString()`. Debugger display and a nested
JSON converter are attached through attributes. The one converter is declared as
`public sealed partial class XJsonConverter : JsonConverter<X>`; the value object's
`JsonConverter` attribute references it directly. It has a public parameterless constructor and
XML documentation on the type and its members. A source-generated `JsonSerializerContext` in
another assembly can construct a public value object's converter without a custom converter or
reflection-based serialization. The public constructor is required for System.Text.Json to
instantiate the attributed converter. Implicit conversions are emitted only when requested. A
string class maps null to null in either conversion,
with `NotNullIfNotNull` annotations, but its constructor rejects null. Converting a null numeric/Guid/Ulid class to its
runtime value throws `ArgumentNullException`. Class equality and comparison operators accept null;
null sorts first and an instance's `Equals(null)` is false.

Strings use the selected ordinal comparer consistently for equality, hashing and ordering;
`OrdinalIgnoreCase` does not alter the stored spelling. String wrappers have no formatted
`ToString` overload. Non-string wrappers implement `ISpanParsable<T>` (including `IParsable<T>`)
and `ISpanFormattable` (including `IFormattable`). They expose string and character-span `Parse`
and `TryParse` overloads with or without a nullable provider. Parsing honors the provider;
formatting delegates to the runtime value. Numbers use `NumericFormat` syntax annotations, Guid
uses `GuidFormat`, and Ulid has none. Guid and Ulid retain `New()` and `Empty`.

**Custom display.** With `ObjectHasCustomToString="true"`, the generator emits neither the
parameterless `ToString()` override nor the `DebuggerDisplay` attribute. The handwritten partial
supplies both, for example a credential value can always display `[redacted]`, including for
`default`. The opt-out applies to both struct and class kinds and all runtime types. It does not
change validation, equality, conversions, the underlying `Value`, JSON serialization, or the
non-string formatting overloads. It controls diagnostic display only; explicit access and wire
serialization still return the underlying value. Omitted or false metadata preserves the default
generated display. Core's `GeneratedRedactedString` fixture demonstrates the checked-in partial:

```csharp
[System.Diagnostics.DebuggerDisplay("[redacted]")]
internal readonly partial struct GeneratedRedactedString
{
    /// <summary>Returns a redacted diagnostic representation.</summary>
    /// <returns>The redaction marker.</returns>
    public override string ToString() => "[redacted]";
}
```

**Validation.** With `ObjectHasIsValidMethod="true"`, supply this public static partial method
in the handwritten partial (substitute the runtime type for `T`):

```csharp
public static partial bool IsValid(
    T value, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? message);
```

The constructor calls it and throws `ArgumentException(message, nameof(value))` on rejection.
The generated declaration owns the `NotNullWhen` attribute; do not duplicate it on the handwritten
implementation of the partial method.
`TryCreate(T value, out X result)` validates once, returning false and default on rejection; a
private constructor creates successful results without repeating validation. String `TryCreate`
accepts `string?` and returns false for null. For classes its result is
`[NotNullWhen(true)] out X?`. A validator must report rejection through its return value and
message, not by throwing. Parsing a syntactically valid but rejected value throws `FormatException`
with that message; `TryParse` returns false. Without this metadata, neither `IsValid` nor
`TryCreate` is generated.

**Default is not validation.** `default(X)` bypasses constructors and `IsValid`. For string
structs it holds a null `Value`, equals another default, hashes to zero, sorts before every
non-null value, and the default generated `ToString()` returns `string.Empty`; a custom display
partial owns that representation when selected. Non-string structs retain the runtime
type's zero/default semantics. A boundary that requires a real value must still reject default,
as `HttpAntiforgery.Create` does for cookie name and path. Validation at assignment cannot enforce
this boundary on its own.

**JSON.** The generated converter reads JSON null as default for structs; the serializer handles
null classes itself. Rejected values and invalid Ulid text throw `JsonException` with the reason.
ULID scalars and dictionary keys must contain exactly 26 characters from the case-insensitive
[ULID alphabet](https://github.com/ulid/spec#canonical-string-representation), with a leading digit
from `0` through `7` to prevent 128-bit overflow. The converter delegates to `Ulid.TryParse`,
which owns this rule for every direct and generated parse path. Malformed text is rejected before
decoding so it cannot alias a valid identifier or silently overwrite a dictionary entry.
String defaults write JSON null. Dictionary property-name conversion is supported for every
runtime type; numbers use invariant culture. A default string wrapper has no valid JSON property
name and cannot be used as a serialized dictionary key. Consumers using source-generated
`JsonSerializerContext` remain NativeAOT-safe.

**Build lifecycle.** `CohesionCreateValueType` runs before `CoreCompile` on every build containing
`CohesionValueType` items. It has no timestamp-based `Inputs`/`Outputs` gate: changes in the task
assembly or imported metadata are observed immediately. The task writes only missing or changed
content, logging one Normal-importance message for each written file. Unchanged files keep their
timestamps so `CoreCompile` stays incremental. It deletes orphaned `*.generated.cs` files only
inside the code-generation output directory. Compile items, IDE `Link` metadata, and the Clean
hook use the same paths. The task stays netstandard2.0 and runs in process under both Visual
Studio's .NET Framework MSBuild and Core MSBuild, using `_CohesionBuildTasksAssembly` as the task
assembly path.

**Metadata diagnostics.** All invalid items are reported before generation fails. Each error names
the item, metadata and allowed values; error text lives in the build task.

| Code | Error |
| --- | --- |
| `COHVT001` | Missing or unknown `ObjectRuntimeType` |
| `COHVT002` | Missing `ObjectNamespace` |
| `COHVT003` | Invalid `ObjectKind` |
| `COHVT004` | Invalid `ObjectAccessModifier`; allowed: `Public`, `Internal`, `None`, or omitted (None and omitted emit no modifier) |
| `COHVT005` | Invalid boolean metadata, including `ObjectHasCustomToString` |
| `COHVT006` | Invalid `ObjectComparison`, or comparison metadata on a non-string type |

**Compatibility with 10.0.0-preview.1.** Regeneration changes `IsValid` from instance to static,
removes string `ToString(format, provider)`, and changes string ordering from current-culture to
ordinal. `HttpCookieDomain` (introduced after preview.1) now compares and hashes ignoring case.
Validated `Parse` now throws `FormatException` on rejection, while `TryParse` returns false.
JSON null now means default, invalid values surface as `JsonException`, and dictionary keys are
supported. Malformed and overflowing ULID JSON strings that preview.1 accepted now throw
`JsonException`. Core's `Ulid.Parse` and `Ulid.TryParse` now reject that malformed and overflowing
text directly too, with `FormatException` and false respectively; every string, character-span,
UTF-8 and provider overload shares the rule. Valid bytes and canonical encoding are unchanged.
Wrong-length `Ulid.Parse` input now throws `FormatException` instead of `ArgumentException`.
Each generated `X.XJsonConverter` is now public, an additive public surface change from
preview.1's non-public nested converter. Public value objects work in source-generated contexts in
other assemblies without consumer-supplied converters. Regeneration conflicts with handwritten
nested converter partials that explicitly declare `internal` or `private` (CS0262); remove a
redundant declaration, or omit its accessibility modifier when it supplies additional members.
The public converter keeps the existing null, invalid-input and dictionary-key behavior. Core's
conformance suite and Http.Cookies' separate test assembly use source-generated contexts without
reflection; the latter treats SYSLIB1220 and SYSLIB1030 as errors to guard converter accessibility
and generated metadata.
`ToString()` on a default string wrapper now returns empty instead of null. These are
intentional source, binary or behavioral changes under the preview compatibility policy.
`ObjectHasCustomToString` is an additive, opt-in metadata option; existing generated types retain
their display behavior unless they select it.

---
## Related Docs

- Build: [Common MSBuild Properties](./MSBUILD_COMMON_PROPS.md)
- Build: [Common MSBuild Targets](./MSBUILD_COMMON_TARGETS.md)
