# EBML overview

This package implements the generic container rules of
[RFC 8794](https://www.rfc-editor.org/rfc/rfc8794).
It supports inspection, streaming production and document editing. Element semantics beyond the
standard EBML header, Void and CRC-32 come from a supplied schema.

## Choose an access mode

| Surface | Input/output | Appropriate use |
| --- | --- | --- |
| `EbmlReader.Create` | Any readable stream | Scan large files or network input; read bounded values, stream raw payloads or skip them |
| `EbmlWriter.Create` | Any writable stream | Emit typed elements; precompute master sizes, stream unknown sizes, or back-patch seekable outputs |
| `EbmlDocument.Load` / `Save` | Seekable source; any writable destination | Edit ordered trees while preserving untouched wire values and lazy binary ranges |
| `EbmlDocument.CreateReader` / `CreateWriter` | Existing Content reader/writer contracts | Synchronous or cancellable asynchronous document loading and saving |

Reader header/scalar operations and writer operations are synchronous. Payload streams also support
asynchronous reads and cancellation. The document contract adapters perform
actual asynchronous I/O in their async methods, including lazy copy-through and CRC verification.

## Define a format

IDs passed to `VInt.FromEncoded` include their wire marker. `VInt.MakeId` instead accepts the
identifier's data bits and is limited to four encoded octets. The active schema can explicitly authorize
an ID such as Matroska's 0x80 even when VInt.IsValidIdentifier rejects it under the generic rule.
Undefined invalid IDs are rejected in both strict and lenient modes. VInt itself has no format exception list.

```csharp
using Assimalign.Cohesion.Content.Ebml;

var rootId = VInt.FromEncoded(0xA0);
var countId = VInt.FromEncoded(0xA1);
var schema = new EbmlSchemaBuilder()
    .Include(EbmlSchema.Header)
    .Add(EbmlElementDefinition.Create(rootId, "Envelope",
        EbmlElementType.MasterElement, unknownSizeAllowed: true,
        minOccurs: 1, maxOccurs: 1))
    .Add(EbmlElementDefinition.Create(countId, "Count",
        EbmlElementType.UnsignedInteger, [rootId],
        minOccurs: 1, maxOccurs: 1, minimumValue: 1UL))
    .Build();
```

The builder snapshots definitions into an immutable lookup. Parent IDs express placement; an empty
parent list means root level. Definitions also carry occurrence bounds, numeric ranges, scalar
defaults, payload-length bounds, unknown-size eligibility, recursion and global status.

A derived package such as Matroska composes its own definitions with `EbmlSchema.Header` and registers
that schema under its DocType using `EbmlSchemaRegistry.Create`. Pass the registry to the reader or
document loader. The parsed header selects the body schema by an ordinal, case-sensitive key.
The test-only `DerivedSchema` fixture demonstrates this extension without changing EBML.

## Construct, edit and save

```csharp
using var document = EbmlDocument.Create(schema);
document.Header.DocType = "example";
var root = EbmlElement.Create(rootId, EbmlElementType.MasterElement);
root.Children.Add(EbmlElement.Create(countId, EbmlElementType.UnsignedInteger, 12UL));
document.Body.Elements.Add(root);
document.Save(destination); // Computes master sizes, including on a non-seekable destination.
```

Leaf `Value` types are exact: `ulong`, `long`, `double`, `string`, or
`Content.Binary.BinarySegment`. Dates are `long` nanoseconds relative to
2001-01-01T00:00:00 UTC, retaining precision that a DateTime tick cannot represent. Masters have a
null value and editable ordered `Children`.

On load, binary and unregistered elements borrow lazy source ranges. Untouched scalar wire bytes
are also retained, preserving explicit widths, padding and floating-point bit patterns.
Assigning `Value` replaces that leaf's preserved bytes; save recalculates parent lengths.
A saved document uses a separate destination. Keep its source open and unchanged until all lazy
values are no longer needed.

## Scan or stream a fragment

With the schema above, a producer can stream an unknown-size master:

```csharp
using var writer = EbmlWriter.Create(output, schema);
writer.BeginMaster(rootId);
writer.WriteUnsignedInteger(countId, 12);
writer.EndMaster();
```

This writes a fragment; use `EbmlDocument` to include the document header. A forward-only consumer
enters the master and reads children until the reader reports that level's end:

```csharp
using var reader = EbmlReader.Create(input, schema);
if (reader.ReadNext())
{
    reader.EnterMaster();
    while (reader.ReadNext())
    {
        if (reader.Current.Identifier.EncodedValue == countId.EncodedValue)
        {
            ulong count = reader.ReadUnsignedInteger();
        }
        else
        {
            reader.Skip();
        }
    }
    reader.LeaveMaster();
}
```

Consume, skip, enter or open a payload stream before advancing. `ReadNext` returning false inside a master
requires `LeaveMaster`; it is not necessarily end of the outer stream. Unknown-size termination
uses schema parentage and retains a sibling header for the enclosing level.

For a current element with known size, stream its raw bytes without a payload-sized allocation:

~~~csharp
long absoluteOrigin = reader.Current.DataOffset;
using (var payload = reader.OpenPayloadStream())
{
    await payload.CopyToAsync(destination, cancellationToken);
}
reader.ReadNext(); // Also drains unread bytes if the payload was consumed only partially.
~~~

The view is forward-only, never reads past the element, and supports asynchronous reads. Its cursor
and SourceOffset are payload-relative; the saved DataOffset is the absolute origin. The view borrows
the reader and becomes invalid when the reader advances or is disposed. Disposing just the view
does not drain it; ReadNext or Skip handles that. Raw access does not interpret scalar values or
master children, so use typed reads and EnterMaster for EBML headers, DocType selection and semantic
validation. Unknown-size masters must be traversed.

For a known size, pass `size:` to `BeginMaster`; `EndMaster` checks the result.
For seekable output, `backPatch: true` reserves a size field. `WriteElement` precomputes a tree's
sizes without buffering its binary payloads. Typed methods accept explicit payload and size widths;
`WriteVoid` writes a padding element whose argument counts payload bytes.

## Plan layouts and preserve original elements

EbmlWriter.GetEncodedLength(element, schema) reports the complete encoded tree length, including
preserved scalar widths and lazy binary sizes, without reading payloads. The value overload accepts
an ID, type, value, schema and optional payload/size widths. Sum child lengths to supply a known
master's data size. The query is not a substitute for schema validation.

EbmlWriter.WriteRawElement(originalSegment) copies a complete original element, including its header.
The source BinarySegment must describe exactly that element; its outer ID, size and schema length
are checked before copying. Children, scalar contents and CRC bytes remain untouched. Unknown-size
masters need an eligible schema and an exact original extent supplied by the caller. The source
must remain open and unchanged, and the destination must use separate storage.

## Validation and limits

Strict mode throws `ContentFormatException` with an error code and byte position.
`EbmlReaderOptions.Lenient` records recoverable schema violations in `Diagnostics`; corrupt scalar
values can be retained as binary in the document model. Framing failures, truncation, unsafe unknown
sizes and safety limits still throw. Strongly typed read methods also throw if a malformed value
cannot produce their return type; use `ReadValue` for lenient binary fallback.

The default buffered-value cap is 16 MiB, depth is 64, document node count is one million and
diagnostic count is 1024. Lazy binary data and streamed payload views are not subject to the buffered-value cap.
A parsed header supplies body ID and size widths; unsupported widths above eight are refused.
Enable `VerifyCrc32` to verify parent checksums while scanning.

See [the design](DESIGN.md) for exact ownership, unknown-size and validation behavior.
