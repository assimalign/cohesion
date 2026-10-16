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

## Anchor and tag boundaries

`YamlNode.Anchor` and `YamlEvent.Anchor` carry `YamlAnchorName?`; their `Tag` properties carry
`YamlTag?`. Both are generated string value objects with the grammar in the handwritten `IsValid`
partial under `ValueObjects/`. Construction and string conversion validate once; parser call sites
use `TryCreate` and retain `YamlException` with source positions for malformed text.

`YamlAnchorName` follows [YAML 1.2.2 §6.9.2](https://yaml.org/spec/1.2.2/#692-node-anchors)'s `ns-anchor-name`: one or more printable non-space
characters excluding the flow indicators `,[]{}`. YAML's character classes govern this rule,
including valid Unicode scalar values; .NET's broader whitespace classification does not. NEL,
NBSP, U+2028 and U+2029 remain valid name characters. The value contains no leading `&` or `*`.
The parser's existing colon-before-space/end limitation remains documented below.

`YamlTag` holds a resolved tag spelling: YAML 1.2.2 §5.6's `ns-uri-char` sequence, including only
complete `%HH` escapes. Under [§6.9.1](https://yaml.org/spec/1.2.2/#691-node-tags), local tags start
with `!`, the non-specific `!` remains valid, and global tags must be well-formed absolute URIs.
Bare names, invalid schemes such as `$:?`, and unescaped brackets in global URI paths are refused.
URI validation does not normalize the stored spelling or decode percent escapes. Single-letter
schemes are expanded only for validation to avoid `System.Uri`'s DOS-drive interpretation.
Named tag handles are resolved by the parser before storage. Local and core tags retain the existing
shorthand when its suffix is legal; valid resolved tags containing shorthand delimiters such as
`,`, `[` or `!` use verbatim `!<...>` form. The existing omission and scalar-kind behavior of
non-specific `!` is unchanged.

YAML anchor and tag spelling is case-sensitive, so generated equality, hashing and ordering use
`ObjectComparison="Ordinal"`; neither type normalizes its stored text. Nullable string conversions
in both directions preserve absence as `null`. `default(YamlAnchorName)` and `default(YamlTag)`
bypass validation, so the node setters refuse a present nullable containing
one of those defaults. A nullable with no value remains valid absence.

`YamlWriterOptions.IndentSize` remains an init-only `int`. `YamlText.CreateWriter` rejects zero or
negative values at writer creation with `ArgumentOutOfRangeException`; direct `YamlText.Write`
calls enforce the same guard before emission. This keeps numeric range validation at composition
without adding a numeric value object.

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
stop at a colon followed by separation or end-of-input for alias-key ergonomics, even though the
anchor-name value grammar permits a colon.

## Error model

`YamlParserOptions` applies to every parse facade, `ParseEvents`, and `CreateReader`. The existing
overloads retain their signatures and use the defaults: `MaxDepth = 512` nested collections
(root collection counts as one), and `MaxAliasExpansion = 1_000_000` expanded alias nodes per
document. Both excesses throw `YamlException` with `LimitExceeded`. Options are init-only;
depth must be positive and the alias budget nonnegative (zero disables aliases).

Depth is checked before recursive collection descent and again against the completed events,
because implicit mapping keys can introduce a parent retroactively. A runtime stack-availability
guard also rejects with `LimitExceeded`, including property-only recursion and callers raising
the depth option beyond the available stack. Thus raising an option cannot disable stack safety,
but unusually small thread stacks may reject below the configured depth.

Aliases still share node instances. An iterative event pass charges each alias the expanded size
of its completed target, counting scalars, collections and mapping keys. It adds cached counts
with saturating arithmetic, never materializing the expansion. Anchors and the budget reset for
each document; duplicate anchor names bind to the most recent declaration. References to an open
ancestor cost one and preserve the existing cyclic graph semantics; consumers must still detect
cycles when traversing nodes. This budget bounds acyclic amplification, not the length of the
input or the number of non-aliased nodes. Parsing continues to buffer the complete source.

Syntax failures use `InvalidStructure`; invalid metadata and escape values use `InvalidValue`;
unterminated flow collections, quotes, tags and escapes use `Truncated`; retained-scope
exclusions (YAML versions, multiline implicit keys, supplementary escapes) use `Unsupported`.
Invalid Unicode escape scalars, including surrogate values and values above U+10FFFF, are
`InvalidValue`. The legacy three-argument `YamlException` constructor now selects
`InvalidStructure`; its offset remains unknown.

Parser-created YAML errors carry one-based line/UTF-16 column and `Position`, the absolute
UTF-16 offset in the original decoded source, including a leading U+FEFF and original CR/CRLF.
This character position follows the root exception's text contract; it is not a byte offset.
Decoder failures below this layer retain their distinct byte positions.

Malformed parsed YAML throws `YamlException : ContentFormatException` with one-based line/column —
the Content-family error contract, so callers catch content failures at area granularity.
Parse(ITextContent) also propagates Content.Text decoding failures unchanged as ContentFormatException
with InvalidValue, the first invalid byte position when known (including BOM bytes), and the
DecoderFallbackException as InnerException. This byte error is not a YAML syntax error and has
no fabricated line/column. Detected encodings and lenient ITextContent preserve replacement
decoding (U+FFFD per maximal ill-formed subpart), then apply normal YAML syntax rules.
Exception messages use the build-generated `ErrorMessages` resources, with parameterized text
formatted using the current culture.
Programmatic invalid anchors and tags throw `ArgumentException` during value construction or
conversion; assigning a wrapped default to a node also throws `ArgumentException`. Non-throwing
callers use the value objects' `TryCreate`. Invalid writer indentation throws
`ArgumentOutOfRangeException` before emission.

## Compatibility with 10.0.0-preview.1

- Parse facades keep their original signatures and add overloads accepting `YamlParserOptions`.
  Existing calls now enforce the default budgets. Both document and event parsing validate alias
  references and expansion cost; event consumers can no longer bypass those checks.
- `YamlException.ErrorCode` is populated for parser failures and the compatibility constructor.
  Parser failures gain original decoded-character offsets; strict decoding still uses byte offsets.
  Signed overflow in eight-digit Unicode escapes is eliminated; invalid Unicode scalars fail as
  `InvalidValue`, and trailing text after a block-context flow node fails as `InvalidStructure`.

- `YamlNode.Anchor`/`Tag` and `YamlEvent.Anchor`/`Tag` change from `string?` to
  `YamlAnchorName?`/`YamlTag?`. These are source and binary breaks. Nullable string conversions
  preserve ordinary string assignments and extraction, but callers using string members directly
  on those properties must extract the string first, and compiled consumers must rebuild.
- Invalid programmatic metadata is rejected with `ArgumentException` at construction, conversion
  or assignment, instead of reaching the emitter and changing token boundaries. A present nullable
  containing a default value object is rejected; `null` still means absent metadata.
- Parsed anchors and tags containing characters outside their YAML grammar, including malformed
  percent escapes in tags, now throw `YamlException` where preview.1 could accept them.
  Empty anchor/alias names still throw `YamlException`, with the shared anchor-grammar diagnostic.
- Global tags that are not well-formed absolute URIs (for example `$:?`, bare names or URI paths
  with unescaped brackets) now throw `ArgumentException` during construction/conversion and
  `YamlException` during parsing. Preview.1 accepted and emitted these invalid tags. Valid local
  tags, the non-specific `!`, and the exact spelling of valid global URIs remain unchanged.
- Zero and negative `IndentSize` values now throw `ArgumentOutOfRangeException` at writer creation
  or direct write entry. Previously creation succeeded, zero could produce ambiguous output, and
  negative values could fail later during indentation.
- Valid resolved local/core tags whose suffix cannot use shorthand now emit verbatim tag syntax
  so their values round-trip. Other valid anchor/tag spellings and safe shorthand output are
  unchanged; the existing non-specific `!` behavior remains unchanged.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` (inherited). Hand-written recursive descent, no
reflection, no regular expressions, no dynamic code.

## Resource-limit regression coverage

Authored depth fixtures cover flow sequences/mappings and compact block sequences/explicit
mapping keys at 1,000, 10,000 and 100,000 levels, plus indented block forms at 1,000. Compact
forms keep the 100,000-level source linear in size instead of allocating quadratic indentation.
Child `dotnet` probes contain stack-overflow regressions; passing probes then repeat in-process.
Additional tests cover the exact 512-level boundary, inserted mapping keys, raised depth settings,
property-only recursion, alias identity/cycles/rebinding/document reset, the nine-by-nine bomb,
allocation/time bounds, and overflow-safe expansion accounting at `long.MaxValue`.

## Non-goals

- YAML 1.1 semantics (`yes/no/on/off` booleans, sexagesimals). `%YAML 1.1` documents parse with 1.2
  core-schema semantics.
- The `merge` key (`<<`), a 1.1 feature by origin; consumers can implement it over the model.
- Streaming (incremental) parsing and comment preservation — add when a consumer needs them.
