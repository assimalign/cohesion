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

See [DESIGN.md](./DESIGN.md) for the architecture and the conformance/known-gaps statement.
