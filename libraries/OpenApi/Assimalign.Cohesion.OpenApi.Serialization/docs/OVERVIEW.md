# Assimalign.Cohesion.OpenApi.Serialization — Overview

## Purpose

Reads and writes Cohesion OpenApi documents as JSON, version-aware and NativeAOT-safe.

## Scope

- `OpenApiJson` — static entry points: `Parse(string|Stream)`, `Serialize(document, version)`,
  `Write(document, stream, version)`.
- `IOpenApiReader` / `IOpenApiWriter` — the reader/writer contracts (with `internal` JSON implementations).
- `OpenApiWriterOptions` — target version override and indentation.
- `document.ToJson(...)` / `document.WriteJson(...)` — ergonomic extension members.

## Dependencies

`Assimalign.Cohesion.OpenApi` (the model) and `System.Text.Json` (in-box). No third-party packages.

## Usage

```csharp
// Author with the model, emit JSON for a target line, read it back.
string json = document.ToJson(OpenApiSpecVersion.V3_1);
OpenApiDocument parsed = OpenApiJson.Parse(json);

// Emit a 3.1 model as 3.0 — version-gated fields adapt automatically.
string v30 = document.ToJson(OpenApiSpecVersion.V3_0);
```

See `docs/DESIGN.md` for the node-tree pipeline and the YAML fast-follow seam.
