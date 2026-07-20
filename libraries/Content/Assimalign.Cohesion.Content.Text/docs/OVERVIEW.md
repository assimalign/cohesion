# Assimalign.Cohesion.Content.Text — Overview

The shared text layer of the Content family: Unicode encoding detection (UTF-8/16/32 by byte order
mark or null-byte pattern), text content over any `IContent` (`ITextContent`), line-oriented reading
with terminator fidelity (`TextLineReader`), and configurable literal tokenization
(`TextTokenizer`) as the base scanning primitive for text-derived format parsers.

## Scope

- `TextEncodingDetector` — pure detection over the first four bytes, YAML 1.2 §5.2 compatible.
- `TextContentFactory` — text content from strings, from content with a known encoding, or from
  content with detection.
- `TextLineReader` / `TextLine` / `TextLineEnding` — lines with one-based numbers and exact endings.
- `TextTokenizer` / `TextToken` / `TextTokenDefinition` / `TextTokenizerOptions` — a stack-only
  tokenizer over `SequenceReader<char>`: caller-registered literal tokens (defaults are the three
  line terminators, overridable), zero-copy text-run slices, optional whitespace runs, and
  line/column/offset positions (`TextPosition`) for parser diagnostics.

## Dependencies

- `Assimalign.Cohesion.Content` (root contracts).

## Usage

```csharp
using Assimalign.Cohesion.Content;
using Assimalign.Cohesion.Content.Text;

// Detect the encoding of reopenable content and read it as text.
using var text = TextContentFactory.FromContent(content);
using var reader = text.OpenText();

// Line-oriented reading with terminator fidelity.
using var lines = new TextLineReader(text.OpenText());
while (lines.TryReadLine(out var line))
{
    Console.WriteLine($"{line.Number}: {line.Text} [{line.Ending}]");
}

// Tokenization for format parsers: register the literals the format cares about.
var options = new TextTokenizerOptions();
options.Tokens.Add(new TextTokenDefinition("#", id: 1));
options.Tokens.Add(new TextTokenDefinition("**", id: 2));

var tokenizer = new TextTokenizer("# Title\n**bold**", options);
while (tokenizer.TryRead(out var token))
{
    // Text runs between matches arrive as TextTokenKind.Text; new-lines keep their exact
    // terminator; token.Value slices the input without copying.
    Console.WriteLine($"{token.Position} {token.Kind} '{token}'");
}
```

See [DESIGN.md](./DESIGN.md) for the retained encoding scope, line-model, and tokenizer decisions.
