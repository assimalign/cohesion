# Assimalign.Cohesion.Content.Binary — Overview

Shared binary primitives for reading, streaming, and manipulating content. The only project
dependency is `Assimalign.Cohesion.Content`, which supplies the malformed-input vocabulary.

- `BinaryContentReader` parses a borrowed span with absolute positions and transactional reads.
- `BinaryContentWriter` emits directly to `IBufferWriter<byte>`, requesting each field's exact size.
- `ContentStreamExtensions` supplies exact reads, skips, and copies, synchronously and asynchronously.
- `ContentSliceStream` bounds nested parsing without copying, on seekable or forward-only sources.
- `BinarySegment` keeps unchanged payloads in memory or as lazy source ranges.
- `FourCC` and the four fixed-point values preserve the raw representation.

A parser buffers only a validated, bounded unit; large payloads remain streams or lazy segments.
For example, read a four-byte field without assuming a stream fills a buffer:

```csharp
using Assimalign.Cohesion.Content.Binary;

Span<byte> bytes = stackalloc byte[4];
source.ReadContentExactly(bytes);
var reader = new BinaryContentReader(bytes, baseOffset: fieldOffset);
uint value = reader.ReadUInt32BigEndian();
```

Use `TryReadExactly` when clean end before a new element is allowed. Partial elements throw
`ContentFormatException` with `Truncated`. Dispose slices and streams you open; keep borrowed
sources open for lazy segments. Materialization always requires an explicit `ToArray(maxLength)` cap.

See [DESIGN.md](DESIGN.md) for cursor, ownership, error-position, and lifetime rules.
