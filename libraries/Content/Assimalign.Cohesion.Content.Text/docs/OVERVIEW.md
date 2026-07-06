# Assimalign.Cohesion.Content.Text — Overview

The shared text layer of the Content family: Unicode encoding detection (UTF-8/16/32 by byte order
mark or null-byte pattern), text content over any `IContent` (`ITextContent`), and line-oriented
reading with terminator fidelity (`TextLineReader`).

## Scope

- `TextEncodingDetector` — pure detection over the first four bytes, YAML 1.2 §5.2 compatible.
- `TextContentFactory` — text content from strings, from content with a known encoding, or from
  content with detection.
- `TextLineReader` / `TextLine` / `TextLineEnding` — lines with one-based numbers and exact endings.

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
```

See [DESIGN.md](./DESIGN.md) for the retained encoding scope and line-model decisions.
