# Assimalign.Cohesion.Content.Markdown — Overview

The Markdown format package of the Content family: a document model, a parser for a documented
subset of CommonMark 0.31.2, an HTML renderer in the spec's output shapes, and a canonical
Markdown writer with a round-trip guarantee. Built on `Assimalign.Cohesion.Content.Text` — both
parser phases run on `TextTokenizer`.

## Scope

- `MarkdownText` — the static facade: `Format`, `Parse` (string, stream with encoding detection,
  or `ITextContent`), `ToHtml`, `Write`, and the content family's reader/writer seams.
- `MarkdownDocument` + node family — sealed blocks (heading, paragraph, block quote, list, code
  block, thematic break) and inlines (literal, emphasis, strong, code span, link, image, line
  break), mutable for programmatic document construction.
- Retained syntax: CommonMark core blocks and inlines per the subset table in
  [DESIGN.md](./DESIGN.md); excluded constructs (setext headings, indented code, raw HTML,
  reference links, GFM extensions) degrade predictably to literal text. Decoded text always
  parses; invalid arguments and strict byte decoding can still fail.
- `MarkdownParserOptions` — configurable container and link-destination depth limits.

## Dependencies

- `Assimalign.Cohesion.Content` (root contracts)
- `Assimalign.Cohesion.Content.Text` (tokenizer, encoding detection, text content)

## Usage

```csharp
using Assimalign.Cohesion.Content.Markdown;

var document = MarkdownText.Parse("# Title\n\nBody with *emphasis* and [a link](/docs).");

// Render for serving.
var html = MarkdownText.ToHtml(document);

// Transform the tree and write canonical Markdown back out.
document.Blocks.Insert(0, new MarkdownThematicBreak());
var markdown = MarkdownText.Write(document);
```

## Resource limits and diagnostics

The defaults are **128 block container levels** and **32 nested parentheses inside a bare link
destination**. The document is excluded from the first count; quotes, lists and list items each
count one. Excess block starts become paragraph text. Over-deep link destinations fail link
recognition and follow ordinary inline parsing. Neither limit rejects text or throws
`LimitExceeded`.

```csharp
var limits = new MarkdownParserOptions
{
    MaxContainerDepth = 64,
    MaxLinkDestinationDepth = 16
};
var boundedDocument = MarkdownText.Parse("> > text", limits);
var reader = MarkdownText.CreateReader(limits);
```

The container cap must be positive; the destination cap can be zero. Invalid options throw
`ArgumentOutOfRangeException` before reading. Inline delimiter runs have no configured cap;
inline model construction and rendering use iterative processing. The parser buffers the whole
source and resulting model. These depth limits do not set a total input-size budget.

Strict `ITextContent` decoding propagates `ContentFormatException` with `InvalidValue`,
the first invalid byte's position when known, and the decoder exception as `InnerException`.
Detected encodings retain replacement decoding. No `MarkdownException` is introduced.

See [DESIGN.md](./DESIGN.md) for the retained subset, the degradation table, the parser phases,
and the round-trip guarantee.
