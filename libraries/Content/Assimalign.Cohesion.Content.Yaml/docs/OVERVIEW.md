# Assimalign.Cohesion.Content.Yaml — Overview

A dependency-free YAML 1.2.2 engine: document model (`YamlStream`/`YamlDocument`/`YamlNode`),
parse-event pipeline (`YamlEvent`), parser, and deterministic emitter, entered through the
`YamlText` facade.

## Scope

- Parse strings, streams (with Unicode encoding detection), or `ITextContent` into documents.
- Write documents back with stable block-style output, quoting, literal blocks, and anchor/alias
  reconstruction for shared nodes.
- Core-schema scalar resolution (null/bool/int/float/string) with typed accessors.
- The content family's `IContentReader<YamlStream>`/`IContentWriter<YamlStream>` seams.
- `YamlParserOptions` — depth and expanded alias budgets shared by document and event parsing.

## Dependencies

- `Assimalign.Cohesion.Content` (root contracts) and `Assimalign.Cohesion.Content.Text` (encoding
  detection). No third-party packages.

## Usage

```csharp
using Assimalign.Cohesion.Content.Yaml;

var document = YamlText.ParseDocument("""
    name: Cohesion
    servers:
      - url: https://api.example.com
    """);

var root = (YamlMapping)document.Root!;
var name = ((YamlScalar)root["name"]).Value;

root.Add("enabled", new YamlScalar(true));
var text = YamlText.Write(document);
```

Anchor and tag metadata use optional validated value objects. Nullable string conversions preserve
`null` as absence, and invalid text throws `ArgumentException` before it can reach the emitter:

```csharp
var service = YamlScalar.FromString("Cohesion");
service.Anchor = "service";
service.Tag = "!service";

string? anchorText = service.Anchor;
string? tagText = service.Tag;
service.Anchor = (string?)null;

if (YamlAnchorName.TryCreate(anchorText, out var anchor))
{
    service.Anchor = anchor;
}
```

`YamlEvent.Anchor` and `YamlEvent.Tag` expose the same `YamlAnchorName?` and `YamlTag?` types.
Use `YamlTag.TryCreate` for non-throwing tag validation. Names omit `&`/`*`; tags contain the
resolved spelling, such as `tag:yaml.org,2002:str` or `!service`, with percent escapes preserved.
Local tags begin with `!`; the non-specific `!` is also valid. Global tags must be well-formed
absolute URIs: bare names and `$:?` are rejected. Validation preserves URI spelling without
normalizing case, paths or percent escapes.
Both types compare case-sensitively. A present `default(YamlAnchorName)` or `default(YamlTag)` is
invalid metadata; use `null` to clear a property.

Writer indentation must be positive. `YamlText.CreateWriter(new YamlWriterOptions { IndentSize = 4 })`
creates a writer with four-space indentation; zero or negative sizes throw
`ArgumentOutOfRangeException` at creation. Direct `YamlText.Write` calls enforce the same guard
before emission.

## Resource limits and diagnostics

All parsing entry points use a default depth of **512 nested collections** and an alias budget of
**1,000,000 expanded nodes per document**. Configure the same limits on direct parsing or a reader:

```csharp
var limits = new YamlParserOptions { MaxDepth = 128, MaxAliasExpansion = 10_000 };
var parsed = YamlText.ParseDocument("key: [one, two]", limits);
var events = YamlText.ParseEvents("key: [one, two]", limits);
var reader = YamlText.CreateReader(limits);
```

Depth must be positive; the alias budget can be zero to disallow aliases. Invalid options throw
`ArgumentOutOfRangeException`. Aliases retain shared node identity. Their budget charges the
expanded target's node count without allocating the expansion; cyclic back-references cost one.
Raising the depth cannot bypass the runtime stack safety guard. Input text is still fully buffered.

Catch `YamlException` (or `ContentFormatException`) and inspect `ErrorCode`:

| Code | Failure |
| --- | --- |
| `LimitExceeded` | Nesting, available execution stack, or alias budget |
| `InvalidStructure` | Invalid syntax or undefined alias |
| `InvalidValue` | Invalid anchor/tag or escape value |
| `Truncated` | Unterminated collection, quoted scalar, tag or escape |
| `Unsupported` | Retained-scope exclusions, including supplementary Unicode escapes |

YAML syntax diagnostics provide one-based `Line`/`Column` and an absolute UTF-16
`Position` in the decoded source, preserving BOM and CRLF offsets. Strict decoding failures
propagate separately with `InvalidValue`, a byte position when known, and the original decoder
exception. The existing three-argument `YamlException` constructor uses `InvalidStructure`
with an unknown offset.

See [DESIGN.md](./DESIGN.md) for the architecture and the conformance/known-gaps statement.
