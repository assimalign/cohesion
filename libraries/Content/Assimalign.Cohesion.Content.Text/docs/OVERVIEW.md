# Assimalign.Cohesion.Content.Text — Overview

The shared text layer of the Content family: Unicode encoding detection (UTF-8/16/32 by byte order
mark or null-byte pattern), text content over any `IContent` (`ITextContent`), line-oriented reading
with terminator fidelity (`TextLineReader`), and configurable literal tokenization
(`TextTokenizer`) as the base scanning primitive for text-derived format parsers.

## Scope

- `TextEncodingDetector` — pure detection over the first four bytes, YAML 1.2 §5.2 compatible.
- `TextContent.Create` — text content from strings, from content with a known encoding, or from
  content with detection.
- `TextLineReader` / `TextLineReaderOptions` / `TextLine` / `TextLineEnding` — bounded lines
  with one-based numbers and exact endings.
- `TextTokenizer` / `TextToken` / `TextTokenDefinition` / `TextTokenizerOptions` — a stack-only
  tokenizer over `SequenceReader<char>`: caller-registered literal tokens (defaults are the three
  line terminators, overridable), zero-copy text-run slices, optional whitespace runs, and
  line/column/offset positions (`TextPosition`) for parser diagnostics.

Creation lives on public sealed TextContent (private constructor); ownership and reopenability
follow the root contracts. Invalid creation arguments use BCL argument exceptions; detection over
single-use content throws ContentException. Messages come from package resources.

## Dependencies

- `Assimalign.Cohesion.Content` (root contracts).

## Usage

```csharp
using Assimalign.Cohesion.Content;
using Assimalign.Cohesion.Content.Text;

// Detect the encoding of reopenable content and read it as text.
using var text = TextContent.Create(content);
using var reader = text.OpenText();

// Line-oriented reading with terminator fidelity.
using var lines = new TextLineReader(text.OpenText(),
    new TextLineReaderOptions { MaxLineLength = 1_048_576 });
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

## Resource limits and diagnostics

The line reader defaults to **1,048,576 UTF-16 code units per line**, excluding its CR, LF or
CRLF terminator. The original constructor uses this limit. Set a positive `MaxLineLength`
to change it; invalid options throw `ArgumentOutOfRangeException` without consuming or owning
the supplied reader.

On excess input, `TryReadLine` throws `ContentFormatException` with `LimitExceeded`
before appending the first excess character. `Position` is its zero-based UTF-16 offset from
the supplied reader's starting point, including preceding terminators. Subsequent reads repeat
the failure without consuming input. Dispose the line reader normally to dispose its source.

Strict decoding errors use `InvalidValue` and byte positions when known; lenient decoding
continues to replace malformed sequences. I/O and cancellation exceptions pass through.
Encoding detection uses a fixed prefix and tokenization returns source slices. The line cap
does not constrain `TextReader.ReadToEnd` or the format packages' whole-input parsers.

See [DESIGN.md](./DESIGN.md) for the retained encoding scope, line-model, and tokenizer decisions.
