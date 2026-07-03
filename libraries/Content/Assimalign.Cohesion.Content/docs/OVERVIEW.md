# Assimalign.Cohesion.Content — Overview

Format-neutral root contracts for the Content library family: content identity and format metadata
(`IContent`, `ContentFormat`, `ContentKind`), stream access with explicit ownership and reopenability,
composition (`IComposableContent`), writability (`IWritableContent`), the format parser/serializer
seam (`IContentReader<TDocument>`/`IContentWriter<TDocument>`), and the area exception root
(`ContentException`, `ContentFormatException`).

## Scope

- Contracts and small immutable descriptors only — no parsing, no serialization, no storage coupling.
- In-memory, stream-backed, and composite content implementations via `ContentFactory`.

## Dependencies

None beyond the base class library.

## Usage

```csharp
using Assimalign.Cohesion.Content;

// Read-only, reopenable content over bytes.
using var content = ContentFactory.FromBytes(bytes, format: yamlFormat, name: "openapi.yaml");
using var stream = content.OpenRead();

// Stream-backed content that borrows (does not dispose) the source.
using var borrowed = ContentFactory.FromStream(networkStream, leaveOpen: true);

// A writable buffer a format writer emits into.
using var buffer = ContentFactory.CreateBuffer();
using (var write = buffer.OpenWrite())
{
    write.Write(payload);
}
```

See [DESIGN.md](./DESIGN.md) for the ownership rules and family layering.
