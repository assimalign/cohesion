# Assimalign.Cohesion.Content.Markdown — Design

## Design intent

The Markdown format package of the Content family: a document model, a parser, an HTML renderer,
and a canonical Markdown writer, built on `Content.Text` (the `TextTokenizer` drives both parser
phases) for the static content engine and any service that stores, transforms, or serves Markdown.
The package implements a **deliberately scoped subset of CommonMark 0.31.2** — the baseline is
named, the subset is enumerated below, and everything outside it degrades predictably to literal
text. Parsing never throws for input: Markdown has no malformed documents, only text.

## The retained subset (baseline: CommonMark 0.31.2)

**Blocks** — ATX headings, paragraphs (with lazy continuation), block quotes, bullet and ordered
lists (tight/loose, marker-change splitting, the interruption rules), fenced code blocks (backtick
and tilde, info strings), thematic breaks, blank-line structure, tab expansion at four-column stops.

**Inlines** — backslash escapes, code spans, emphasis and strong emphasis (the delimiter-stack
algorithm with left/right-flanking rules, intraword-underscore restrictions, and the
multiple-of-three rule), inline links and images (angle and balanced-paren destinations, all three
title forms), URI autolinks, hard breaks (two-space and backslash), soft breaks, numeric character
references, and the five XML-predefined named entities (`&amp;` `&lt;` `&gt;` `&quot;` `&apos;`).

**Excluded, with the documented degradation** (issue #468 requires unsupported constructs to fail
or degrade *predictably*):

| Excluded construct | Behavior in this package |
|---|---|
| Setext headings | The underline stays paragraph text (`===`), or parses as a thematic break (`---`) |
| Indented code blocks | Indentation is stripped like any paragraph line; text joins the paragraph |
| Raw HTML (blocks and inline) | Literal text, entity-escaped on HTML output |
| Link reference definitions and reference links | Literal text (`[a][b]` renders as written) |
| The full HTML5 named-entity table | Unknown names stay literal (`&auml;` renders as `&amp;auml;`) |
| Email autolinks | Literal angle text |
| GFM extensions (tables, strikethrough, task lists) | Literal text — CommonMark core only |

Two deliberate exclusions carry the rationale: **raw HTML** is the injection surface of Markdown —
a static content engine serving parsed output must not pass author HTML through by default, and an
opt-in sanitized pipeline can layer on later; **indented code** is the spec's largest source of
parser complexity and accidental-input surprise, and fence-first Markdown is the modern corpus
(the same call MDX v2 made). Both are recorded here so "should we add X?" starts from the reason
it was cut.

## Two-phase parser on the shared text layer

The block phase (`MarkdownBlockParser`) consumes lines from a `TextTokenizer` with default options
(the line model), maintaining the CommonMark open-container stack: match continuation markers
outermost-in, try new block starts, else paragraph text with lazy continuation. Leaf raw content
is collected during the phase and inline-parsed only after the tree is complete. The inline phase
(`MarkdownInlineParser`) tokenizes each leaf's content through a `TextTokenizer` delimiter table
(`` ` `` `*` `_` `[` `]` `!` `<` `\` `&`), assembling delimiter *runs* from adjacent single-char
tokens by offset contiguity — precedence follows the spec: code spans and autolinks bind in the
left-to-right pass, brackets resolve at `]`, emphasis resolves last via the delimiter stack.

This is the tokenizer's first consumer, and the two phases are the pattern future text formats
should copy: default table for line structure, custom literal table for inline delimiters.

## Model shape

`MarkdownNode` is the abstract root with a `private protected` constructor — the node set is
closed, like the YAML package's. Blocks (`MarkdownHeading`, `MarkdownParagraph`,
`MarkdownBlockQuote`, `MarkdownList`, `MarkdownCodeBlock`, `MarkdownThematicBreak`) and inlines
(`MarkdownLiteral`, `MarkdownEmphasis`, `MarkdownStrong`, `MarkdownCodeSpan`, `MarkdownLink`,
`MarkdownImage`, `MarkdownLineBreak`) are sealed; `MarkdownDocument` and `MarkdownListItem` are
the two structural containers outside those families (items exist only inside lists). Nodes are
mutable with validating setters, so documents can be built or rewritten programmatically — the
static content engine will transform trees, not strings. Escapes and entities are resolved at
parse time; `MarkdownLiteral.Text` and link destinations hold final, unencoded values, and each
renderer applies its own encoding.

`MarkdownText` is the static facade (the `YamlText` convention): `Format` descriptor,
`Parse(string | Stream | ITextContent)`, `ToHtml`, `Write`, and the `IContentReader<>`/
`IContentWriter<>` seams.

## Error model: parsing never throws

Unlike YAML (which has malformed input and a `YamlException`), Markdown assigns meaning to every
input, so this package defines **no exception root** — the parser cannot fail, and renderer/model
argument misuse throws BCL argument exceptions. There is deliberately no
`MarkdownException`; a future need for one should be treated as a design smell (it would mean the
parser stopped being total).

## Robustness posture

- **Container depth caps at 128** during parsing; deeper markers degrade to paragraph text. This
  bounds block nesting from hostile input.
- **Inline nesting is unbounded by parsing** (emphasis wraps bottom-up), so **both renderers walk
  iteratively** with explicit op stacks — no recursion anywhere in the package, and caller-built
  trees of any depth render without stack overflow.
- `U+0000` becomes `U+FFFD` at inline parse, per the spec's insecure-characters rule.

## Renderers

**HTML** (`MarkdownHtmlRenderer`) emits the shapes the spec's examples use — tight list items
render bare inline content, loose items wrap paragraphs, image alt text flattens to plain text,
destinations percent-encode over the reference implementation's safe set and entity-escape in
attributes — so spec-derived expectations assert byte-for-byte.

**Canonical Markdown** (`MarkdownWriter`) normalizes on output: `-` bullets, `N.` ordered markers,
ATX headings, backtick fences (tilde when the info string carries a backtick), `---` breaks,
emphasis delimiters alternating `*`/`_` by nesting depth (so nested emphasis never fuses into
`**`), conservative literal escaping (structural characters always; block markers at line start;
`!` only before a bracket), and prefix-stack container writing where a quote opening mid-line
(first block of a list item) emits its marker inline. The invariant tests enforce:
**write → parse → write is a fixed point, and parse → write → parse preserves the rendered HTML.**
The guarantee covers parser-produced trees; hand-built trees can express forms with no Markdown
spelling (emphasis around whitespace) and round-trip only when they stay inside what the parser
can produce.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` (inherited). Hand-written scanning over spans and the
`TextTokenizer`; no regular expressions, no reflection, no dynamic code.

## Non-goals

- Raw-HTML passthrough or an HTML sanitizer — a service-layer concern to design with the static
  content engine, not default parser behavior.
- GFM extensions (tables, strikethrough, task lists, autolink-literals) — candidates for later
  features on this package once the engine needs them; each would extend the block or inline phase
  behind its own switch, not relitigate the core.
- Reference links and definitions — excluded until a real consumer needs cross-reference
  resolution; adding them means a definition pass between the block and inline phases.
- Source-position tracking on nodes (`TextPosition` spans) — the tokenizer provides positions, but
  carrying them on every node is deferred until a consumer (linting, editor tooling) exists.
- Front matter (YAML metadata blocks) — the static content engine composes `Content.Yaml` with
  this package at the document boundary; the Markdown parser does not special-case it.
