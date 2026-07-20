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
  reference links, GFM extensions) degrade predictably to literal text. Parsing never throws.

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

See [DESIGN.md](./DESIGN.md) for the retained subset, the degradation table, the parser phases,
and the round-trip guarantee.
