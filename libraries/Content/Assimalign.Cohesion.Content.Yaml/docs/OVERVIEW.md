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
Both types compare case-sensitively. A present `default(YamlAnchorName)` or `default(YamlTag)` is
invalid metadata; use `null` to clear a property.

Writer indentation must be positive. `YamlText.CreateWriter(new YamlWriterOptions { IndentSize = 4 })`
creates a writer with four-space indentation; zero or negative sizes throw
`ArgumentOutOfRangeException` at creation. Direct `YamlText.Write` calls enforce the same guard
before emission.

See [DESIGN.md](./DESIGN.md) for the architecture and the conformance/known-gaps statement.
