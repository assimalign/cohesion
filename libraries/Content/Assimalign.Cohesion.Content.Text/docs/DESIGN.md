# Assimalign.Cohesion.Content.Text — Design

## Design intent

The shared text layer of the Content family: how encoded bytes become characters (`ITextContent`,
`TextEncodingDetector`) and how characters become lines (`TextLineReader`). Text-derived format
packages (Markdown, YAML) and services that serve or inspect text build on this layer instead of each
re-implementing encoding detection and line handling.

## Retained encoding scope

Unicode only: **UTF-8 (the default), UTF-16 LE/BE, and UTF-32 LE/BE.** Legacy code pages are a
deliberate non-goal — callers that must consume them decode externally and hand this layer decoded
text. All `Encoding` instances the layer produces are configured to never emit byte order marks.

Detection (`TextEncodingDetector.Detect`) is a pure function over the first four bytes:

1. **Byte order marks**, longest first (UTF-32 marks contain the UTF-16 LE mark as a prefix).
2. **Null-byte patterns** for markless Unicode streams whose first character is ASCII — the scheme the
   YAML 1.2 specification (§5.2) standardizes, which generalizes to any ASCII-leading text format.
3. **UTF-8 default** otherwise.

The result carries the preamble length so decoding starts after the mark. Detection through
`TextContentFactory.FromContent(content)` requires *reopenable* content because it consumes a read;
single-use content must supply its encoding explicitly — an explicit contract instead of a silently
half-consumed stream.

## Text content

`ITextContent : IContent` adds the known `Encoding` and `OpenText`/`OpenTextAsync`, which follow the
root family's ownership rules: readers are caller-owned, valid while the content is undisposed, and
always start at the beginning of the text (after any byte order mark). The implementation wraps any
`IContent`, so text semantics layer over memory, stream, or future storage-adapter content without
duplicating source handling.

## Line model

`TextLineReader` produces `TextLine` values: content without the terminator, a one-based line number,
and which terminator ended the line (`\n`, `\r\n`, lone `\r`, or none at end of text).

- **No newline normalization and no Unicode normalization.** Lines are reported exactly as authored;
  the `Ending` field lets format packages implement their own policies (a Markdown renderer may
  normalize, a YAML round-trip must not) without this layer guessing.
- Empty input yields zero lines, and a trailing terminator does not create a phantom final line —
  `"a\n"` is one line, matching how line-oriented specifications count lines.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` (inherited). Pure decoding over BCL `Encoding`/
`StreamReader`; no reflection, no dynamic code.

## Non-goals

- Legacy (non-Unicode) encodings and charset conversion.
- Unicode normalization (NFC/NFD) — a format- or service-level decision.
- Grapheme/word segmentation — nothing in the family needs it yet; add when a consumer exists.
- The Markdown document model — that builds *on* this layer in `Content.Markdown` (#468).
