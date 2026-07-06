# Assimalign.Cohesion.Content.Yaml — Design

## Design intent

A dependency-free YAML 1.2.2 engine for the Content family: document model, parse-event pipeline,
parser, and emitter. Its first consumer is OpenApi YAML serialization; its design consumer is any
service that needs standards-driven YAML without a third-party dependency (the repo deliberately
carries no YamlDotNet — parsing wire formats is what the Content area *is*).

## Architecture

```
text ──► YamlEventParser ──► YamlEvent stream ──► YamlComposer ──► YamlDocument/YamlNode
                                                                        │
text ◄──────────────────────── YamlNodeEmitter ◄────────────────────────┘
```

- **`YamlEventParser`** (internal) merges scanning and parsing into one recursive-descent pass over a
  normalized character cursor (`\r\n`/`\r` → `\n`, BOM stripped, one-based positions). The classic
  simple-key problem — `a: b` only becomes a mapping when the `:` is seen — is solved by parsing the
  candidate node first and *inserting* a `MappingStart` event before it when a `:` follows, instead
  of libyaml's token-queue lookahead machinery.
- **`YamlEvent`** is the public pipeline (#814's acceptance surface): stream/document/collection
  boundaries, scalars, and aliases, each with line/column. Streaming consumers can use
  `YamlText.ParseEvents` without paying for the node tree.
- **`YamlComposer`** (internal) builds nodes and resolves aliases to **shared node instances** —
  shared structure is reference identity, registered before children compose so self-referential
  structures work. The emitter reverses this: any node reachable twice gets an anchor on first
  occurrence and aliases after.
- **`YamlNodeEmitter`** (internal) writes deterministic output: block styles by default, flow where a
  collection asks for it or is empty, plain scalars where safe, double quotes where not, and literal
  blocks for multi-line strings with the chomping indicator chosen to reproduce trailing breaks.
  Strings that would re-resolve as other kinds (`"true"`, `"42"`) are quoted so kinds round-trip.

Parsing reads the whole input up front. YAML documents are configuration-sized; whole-input
processing keeps encoding detection (delegated to `Content.Text`, YAML §5.2-compatible), position
tracking, and multi-document semantics simple. A streaming scanner is a non-goal until a consumer
needs one.

## Scalar model

`YamlScalar` carries the decoded `Value`, the presentation `Style`, and a resolved core-schema
`Kind` (`YamlCoreSchema`, spec §10.2: null/bool/int/float forms incl. `0x`/`0o` and `.inf`/`.nan`).
Kind resolution applies to plain scalars only; quoted and block scalars are strings; explicit
`tag:yaml.org,2002:*` tags override. `YamlScalar.FromString` pins the string kind so ambiguous text
survives round-trips. Mappings preserve entry order and allow arbitrary nodes as keys (string-keyed
convenience access covers the dominant case).

## Retained scope and known gaps

Supported: block and flow collections, all five scalar styles with escapes and folding, anchors and
aliases, tags (`!`, `!!`, named handles via `%TAG`, verbatim), directives, comments, multi-document
streams, explicit `?` keys, and single-pair flow mappings.

The honest conformance number lives with the vendored corpus
([tests/TestData/yaml-test-suite/README.md](../tests/TestData/yaml-test-suite/README.md)): **223 of
333 runnable official cases (67%)** at vendoring time, with every excluded case id documented in
three groups — valid-but-rejected (tab separation, exotic indentation), divergent semantics (folding
subtleties, empty keys), and lenient acceptance of invalid input (strictness is the long tail; this
parser prefers accepting near-YAML over rejecting valid YAML). The corpus is a regression floor:
gap fixes move cases in, and cases never silently drop out.

Additional deliberate gaps: escapes beyond U+FFFF (`\U`) are rejected rather than paired; multi-line
implicit keys are unsupported (the spec caps implicit keys at one line in practice); anchor names
stop at `: ` for alias-key ergonomics.

## Error model

Everything malformed throws `YamlException : ContentFormatException` with one-based line/column —
the Content-family error contract, so callers catch content failures at area granularity.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` (inherited). Hand-written recursive descent, no
reflection, no regular expressions, no dynamic code.

## Non-goals

- YAML 1.1 semantics (`yes/no/on/off` booleans, sexagesimals). `%YAML 1.1` documents parse with 1.2
  core-schema semantics.
- The `merge` key (`<<`), a 1.1 feature by origin; consumers can implement it over the model.
- Streaming (incremental) parsing and comment preservation — add when a consumer needs them.
