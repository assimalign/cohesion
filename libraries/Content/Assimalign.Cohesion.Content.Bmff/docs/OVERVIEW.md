# Assimalign.Cohesion.Content.Bmff

BMFF supplies the reusable ISO base media container machinery beneath MP4 and HEIF profiles.
It references Content.Media and consumes Content.Binary's bounded streams, exact reads, FourCC
and lazy BinarySegment payloads. It does not decode media.

## Scan without materializing media

```csharp
using var reader = BmffReader.Create(input);
while (reader.Read())
{
    var header = reader.Header;
    if (header.BoxType == BmffBoxType.Movie)
    {
        using var children = reader.ReadChildren();
        while (children.Read())
        {
            Inspect(children.Header);
        }
    }
}
```

Input may be non-seekable. Read acquires only a header; advancing skips unread payloads.
A seekable skip reads no media bytes. A non-seekable skip must drain those bytes through a fixed
buffer. Payload is available for direct consumption before advancing. Current materializes a box;
lazy media/unknown materialization requires seekable input.

## Load, edit and save

```csharp
using var document = BmffDocument.Load(input);
var fileType = (FileTypeBox)document.Boxes[0];
fileType.CompatableBrands = new[] { FourCC.Parse("isom").Value };
await document.SaveAsync(output, cancellationToken);
```

Load requires seekable input. Unchanged media and opaque boxes remain lazy and copy through on save.
The original misspelling CompatableBrands remains public for compatibility.
Keep the source open and unchanged until saving finishes, and use a separate destination.
The document borrows the source unless Load receives leaveOpen: false. Reader and writer instances
always borrow supplied streams. These objects and their sources are used sequentially.

## Produce boxes incrementally

```csharp
using var writer = BmffWriter.Create(output);
writer.BeginContainer(BmffBoxType.MovieFragment, payloadSize: knownChildBytes);
writer.Write(fragmentChild);
writer.EndContainer();
```

Supply the payload size for direct streaming, or omit it to buffer the container within the
configured cap. WritePayload copies raw segment bytes when a producer supplies a box prefix or
opaque content. The output need not seek.

## Extend through a registry

```csharp
var registry = BmffBoxRegistry.CreateBuilder()
    .Register(profileType, header => new ProfileBox(header))
    .Build();
using var reader = BmffReader.Create(input, new BmffReaderOptions(), registry);
```

A profile implements its own per-box Read/Write and GetPayloadSize, and keeps the existing visitor
and composite model. The factory receives a validated header. Builder snapshots are independent.
See [DESIGN](DESIGN.md) for custom composite prefixes and the three access modes.

## Scope and compatibility

The package implements structural containers, ftyp, free/skip and lazy mdat. Unknown types and
deferred standard leaf models load as UnknownBox in lenient mode; strict mode rejects them.
Their bytes and original header encodings survive unchanged save.

Reader defaults are 16 MiB per buffered leaf and depth 64. BmffStream now follows window-relative
Position/Length semantics and retains an absolute Offset. Children are mutable lists.
BmffWriterCallback was replaced by size-aware container operations and WritePayload.

The standard field models and dependent media-offset recalculation belong to #447. Moving media
or changing the layout around preserved stco/co64 is not yet a supported relocation operation.
[DESIGN](DESIGN.md) lists every deferred model and the complete error/ownership contract.
