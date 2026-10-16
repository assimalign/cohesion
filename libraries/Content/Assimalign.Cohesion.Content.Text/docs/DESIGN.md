# Assimalign.Cohesion.Content.Text — Design

## Design intent

The shared text layer of the Content family: how encoded bytes become characters (`ITextContent`,
`TextEncodingDetector`), how characters become lines (`TextLineReader`), and how characters become
tokens (`TextTokenizer`). Text-derived format packages (Markdown, YAML) and services that serve or
inspect text build on this layer instead of each re-implementing encoding detection, line handling,
and delimiter scanning.

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
`TextContent.Create(content)` requires *reopenable* content because it consumes a read;
single-use content must supply its encoding explicitly — an explicit contract instead of a silently
half-consumed stream.

## Text content

`ITextContent : IContent` adds the known `Encoding` and `OpenText`/`OpenTextAsync`, which follow the
root family's ownership rules: readers are caller-owned, valid while the content is undisposed, and
always start at the beginning of the text (after any byte order mark). The implementation wraps any
`IContent`, so text semantics layer over memory, stream, or future storage-adapter content without
duplicating source handling. Public sealed TextContent owns the concrete-returning Create overloads
for strings, explicitly encoded content, and detected content; its constructor is private (decision 59).

## Line model

`TextLineReader` produces `TextLine` values: content without the terminator, a one-based line number,
and which terminator ended the line (`\n`, `\r\n`, lone `\r`, or none at end of text).

- **No newline normalization and no Unicode normalization.** Lines are reported exactly as authored;
  the `Ending` field lets format packages implement their own policies (a Markdown renderer may
  normalize, a YAML round-trip must not) without this layer guessing.
- Empty input yields zero lines, and a trailing terminator does not create a phantom final line —
  `"a\n"` is one line, matching how line-oriented specifications count lines.

`TextLineReader` previously accumulated an arbitrarily long line in a `StringBuilder`.
`TextLineReaderOptions.MaxLineLength` now defaults to 1,048,576 UTF-16 code units, excluding
CR/LF terminators. A positive init-only value can raise or lower the cap; the original constructor
uses defaults. The reader consumes the first excess character and throws `ContentFormatException`
with `LimitExceeded` before appending it. No subsequent read consumes input; it repeats the
failure. `Position` is that character's zero-based offset from the supplied reader's initial
position, including preceding terminators. A `TextReader` has no byte origin, so this is a
character offset, not a fabricated byte position. Disposing the line reader still disposes its
source, including after failure.

Detection uses a fixed prefix and the tokenizer returns slices without accumulating input-sized
text buffers, so neither needs a new buffer option. `TextContent` exposes a `TextReader`;
callers choosing `ReadToEnd` still own their total materialization policy. The line limit does
not apply to those calls or to YAML/Markdown's separate whole-input parsing paths.

## Tokenizer

`TextTokenizer` is the base scanning primitive format parsers build on (the Markdown parser, #468,
is the first consumer; the static content engine follows): a stack-only `ref struct` over
`SequenceReader<char>` that splits text into `TextToken` values — matches of caller-registered
literal tokens, and the runs of text between them, every value a zero-copy slice of the input.

- **A literal token table, not rules or regular expressions.** `TextTokenizerOptions.Tokens` holds
  `TextTokenDefinition` values: exact texts with a caller-assigned `Id` for parser dispatch and a
  `TextTokenKind` category. Matching is ordinal, leftmost, and longest-first among definitions
  sharing a first character (`**` beats `*`), so delimiter-run counting stays a parser concern
  (CommonMark-style) while the tokenizer stays predictable and vectorizable — text runs advance via
  `TryAdvanceToAny` over the definitions' first characters. Lexical rules (numbers, identifiers)
  are a parser-layer concern applied to text runs.
- **The defaults are the three line terminators as removable definitions** (`\r\n`, `\n`, lone
  `\r` — the same recognition set as `TextLineReader`). Overriding the defaults *is* editing the
  list: with default options tokenization degenerates to the line model (text runs separated by
  `NewLine` tokens with terminator fidelity); removing a terminator lets it flow through text runs.
- **Whitespace runs are a rule, not a literal** — a run of one-or-more spaces/tabs cannot be
  expressed as an exact text. `TokenizeWhitespace` opts in; definitions win over runs, including
  literals that start with whitespace mid-run. Scope is ASCII space and tab: Unicode whitespace
  classes are a format decision.
- **Positions are physical and table-independent.** Emitted values are scanned for `\n`/`\r\n`/lone
  `\r` (a carriage return carries across value and segment boundaries so a split `\r\n` counts
  once), so `TextPosition` (one-based line/column, zero-based char offset) stays correct even when
  the new-line defaults are removed and terminators travel inside text runs. A `\n` completing a
  carriage return across a token boundary reports at the start of the new line; the break still
  counts once. Columns count UTF-16 code units. Positions exist to feed parser diagnostics
  (`ContentFormatException.Position`); the tokenizer itself never throws for input — it has no
  notion of malformed text.
- **Ref struct out, plain structs in flight.** The tokenizer is stack-only, but `TextToken` holds a
  `ReadOnlySequence<char>` slice and is an ordinary struct parsers can store and materialize
  (`ToString()`) on demand. Text runs are emitted before the match that terminated them (the match
  is held internally), so consumers always see document order.
- **Construction compiles the table.** The options snapshot (grouped by first character, longest
  first) happens in the constructor — construct one tokenizer per text, not per line; later options
  mutations don't affect live tokenizers. Invalid tables (null entries, duplicate texts) throw
  `ArgumentException` at construction, never during reads.

## Error model

Line-buffer limit failures use `ContentFormatException` with `LimitExceeded` and decoded
character offsets as described above. Exact-limit tests cover EOF and each terminator, original
UTF-16 positions across CRLF and surrogate pairs, raised caps, repeated failures and disposal.
An endless generated line proves that reads and allocation stop at the configured budget.

The tokenizer accepts every input and reports positions for format parsers. Invalid tokenizer options
and creation arguments use BCL argument exceptions; detecting single-use content throws ContentException.
Strict caller-supplied decoders report malformed byte sequences as ContentFormatException with InvalidValue,
preserving DecoderFallbackException as InnerException. An internal decoder adapter translates at the
StreamReader decoding boundary, covering all sync/async reads, lines, blocks and reads to EOF.
Position identifies the first invalid byte, including BOM bytes and sequences split across reads.
It uses the underlying seekable stream origin, or byte zero for a forward-only source; a decoder
index that cannot identify a nonnegative byte offset produces a null Position. I/O and cancellation
exceptions retain their original types. The reader still owns only the underlying content read view.

Detected encodings and the default UTF-8 path remain lenient: each Unicode maximal ill-formed
subpart becomes U+FFFD, including an incomplete sequence flushed at EOF. Caller replacement
fallbacks are preserved. No Unicode normalization is performed; NEL (U+0085), LS (U+2028),
PS (U+2029), VT and FF stay inside lines, rather than extending the CR/LF/CRLF recognition set.

Package-authored messages are build-generated ErrorMessages resources. Format readers above this layer
use the root ContentFormatException.ErrorCode and Position; legacy exceptions retain Unspecified.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` (inherited). Pure decoding over BCL `Encoding`/
`StreamReader` and span/sequence scanning; no reflection, no dynamic code.

## Non-goals

- Legacy (non-Unicode) encodings and charset conversion.
- Unicode normalization (NFC/NFD) — a format- or service-level decision.
- Grapheme/word segmentation — nothing in the family needs it yet; add when a consumer exists.
- Rule- or regex-based token definitions and Unicode whitespace classes — the literal table plus
  the whitespace rule cover structural delimiters; richer lexing belongs to format parsers.
- A UTF-8 byte-level tokenizer (`SequenceReader<byte>`) — worth adding only when a consumer parses
  encoded bytes directly instead of decoded text.
- Streaming/incremental tokenization over `TextReader` — the tokenizer consumes an in-memory
  `ReadOnlySequence<char>`; buffered bridging from streams is the shared-primitives feature
  (#438).
- The Markdown document model — that builds *on* this layer in `Content.Markdown` (#468).
