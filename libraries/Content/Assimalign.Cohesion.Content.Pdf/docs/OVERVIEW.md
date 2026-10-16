# PDF package overview

`Assimalign.Cohesion.Content.Pdf` reads, edits and saves PDF structure. The public reader and writer
implement the root `IContentReader<PdfDocument>` and `IContentWriter<PdfDocument>` contracts.
`Content.Pdf` references `Content.Binary`, which supplies lazy `BinarySegment` ranges,
exact reads and byte primitives; Binary references the neutral Content contracts.

## Read a document

```csharp
using System.IO;
using Assimalign.Cohesion.Content;
using Assimalign.Cohesion.Content.Pdf;

using var source = File.OpenRead("document.pdf");
IContentReader<PdfDocument> reader = PdfReader.Create(new PdfReaderOptions
{
    MaxDecodedBytes = 16 * 1024 * 1024,
    MaxDepth = 128,
    IsLenient = false
});
using var document = reader.Read(source);
foreach (var page in document.Pages)
{
    // Resources, MediaBox, CropBox and Rotate include inherited values.
    var resources = page.Resources;
    var originalDictionary = page.Dictionary;
}
```

The stream's current position is the PDF origin. Xref offsets are relative to that origin;
format errors report source positions, including enclosing `ContentSliceStream` offsets.
The header must begin within the first 1024 bytes from the origin. A bounded backward scan
finds the last `startxref` and `%%EOF`. A non-seekable or unreadable source fails with
`ContentFormatException(Unsupported)`; the reader never buffers an entire source to emulate seeking.

`ReadAsync(source, cancellationToken)` returns a `ValueTask<PdfDocument>` and uses synchronous
random-access parsing, checking cancellation before loading and between source-buffer fills.
It does not schedule background work. The source stays open after a failed load. On success,
`leaveOpen: false` transfers disposal responsibility to the document; default `true` borrows it.
Operations over one document/source are sequential. Do not mutate the source's byte layout.

## Inspect and edit values

`IPdfObject` is the common contract. The sealed concrete forms are `PdfNull`, `PdfBoolean`,
`PdfInteger`, `PdfReal`, `PdfString`, `PdfName`, `PdfArray`, `PdfDictionary`, `PdfStream` and
`PdfIndirectReference`. Parameterized construction uses `Create`; empty arrays, dictionaries,
nulls, readers and options use parameterless constructors. `PdfName.Parse` accepts slash-prefixed
encoded name bytes.

```csharp
var name = PdfName.Parse("/Display#20Name"u8);
var values = new PdfDictionary();
values[name] = PdfString.Create(new byte[] { 0x00, 0xFF, 0x41 }, isHexadecimal: true);
values[PdfName.Create("Enabled"u8)] = PdfBoolean.Create(true);
var identity = PdfObjectIdentifier.Create(12, generation: 0);
IPdfObject value = document.Resolve(identity);
```

Arrays and dictionaries are editable. Replacing a dictionary value preserves its insertion order.
Names compare decoded bytes, case sensitively. Strings retain decoded bytes and literal/hexadecimal
representation; `DecodeText(Encoding)` is an explicit helper, not automatic PDF text extraction.
Integers use `Int64`; reals use finite `Double`. Indirect reference equality includes generation.
Resolution caches successful in-use objects by identity. Missing/free/obsolete-generation references
produce PDF null, as required by §7.3.10.

`document.Trailer` retains newest-first merged entries. `document.CrossReferences` includes free
entries, their generations and next-free links, source offsets, and compressed-object locations.
`StartXref` records the last xref offset, or -1 after repair. `Catalog` resolves the trailer Root;
`Pages` creates a fresh ordered inherited-attribute view. `PageCount` walks the tree rather than
trusting a declared Count. Obtain a new Pages view after editing its dictionaries.

Resolved arrays and dictionaries track nested in-place edits automatically. `Add(value)` returns a
new generation-zero `PdfObjectIdentifier` above the highest known/reserved number. `Replace(id, value)`
retains a current live identity; `Free(id)` removes it and increments its generation up to 65535.
Missing, free or obsolete identities cannot be replaced/freed. Free numbers are not reused.
Trailer edits use its existing dictionary indexer and Remove. Pending additions/deletions appear in
CrossReferences, but saved offsets and free links are available only after reloading the output.

## Save a document

```csharp
var info = new PdfDictionary();
info[PdfName.Create("Title"u8)] = PdfString.Create("Revised document"u8);
var infoId = document.Add(info);
document.Trailer[PdfName.Create("Info"u8)] = PdfIndirectReference.Create(infoId);

// A resolved container edit is tracked without a Replace call.
document.Catalog[PdfName.Create("PageMode"u8)] = PdfName.Create("UseOutlines"u8);

using var destination = File.Create("saved.pdf");
IContentWriter<PdfDocument> writer = PdfWriter.Create(new PdfWriterOptions
{
    UseCrossReferenceStream = true, // PDF 1.5+; false selects a classic table.
    PackObjects = true
});
writer.Write(destination, document);
```

The writer leaves both streams open. The destination's current position becomes the output origin;
it may be non-seekable and must have no trailing bytes. Use storage distinct from the source, including
any wrapper or second file handle. A full save writes every live object, recalculates xref offsets and
stream Length, and preserves object identities, pages and inherited attributes. Encryption fails with
`ContentFormatException(Unsupported)` before any output, even if Encrypt was removed after loading.

For an incremental save, use a fresh destination and set `Incremental = true`:

```csharp
using var revised = File.Create("revised.pdf");
var incrementalWriter = PdfWriter.Create(new PdfWriterOptions { Incremental = true });
incrementalWriter.Write(revised, document);
```

This copies the original PDF range byte-for-byte and appends changed/new objects and a Prev-linked
xref section. Its table/stream/hybrid style follows the source's last section; full-save packing and
xref choices are ignored. Deleted objects receive free entries with incremented generations. Removed
trailer keys use PDF null to override older entries. Recovered documents require a full save first.
An incremental edit cannot lower a catalog Version that upgraded the unchanged source header;
that edit fails explicitly. A full save retains the effective version in its new header.
In-place append is not supported. Edit compressed members individually; changing their object-stream
container while unchanged members still depend on it fails loudly.

Saving does not clear edits or rebase the document. Repeated saves with identical model/order and options
produce identical bytes; reload a saved result before building the next revision. Both existing ID strings
are retained. Changing/removing the loaded first identifier fails explicitly; an absent ID is generated
deterministically. Save errors or cancellation can leave partial destination output; discard it.
`WriteAsync(destination, document, cancellationToken)` returns ValueTask and currently uses synchronous
I/O, checking cancellation between bounded operations. It does not schedule background work.

## Read stream bytes

`PdfStream.Dictionary` preserves the stream's metadata. Before replacement, `Data` is the original encoded
`BinarySegment`; opening/copying it does not invoke filters. `Decode()` returns bounded decoded
memory on demand and reflects the current filter dictionary. It supports Flate, ASCIIHex,
ASCII85, LZW, RunLength, TIFF prediction and all PNG row predictors. A decode call is not cached.
Image filters remain available as encoded bytes and return `Unsupported` when decoding is requested.
All strings and stream bytes belonging to an encrypted document return `Unsupported` on access,
including trailer strings; cross-reference information remains loadable.

`ReplaceData(BinarySegment)` accepts borrowed **unencoded** replacement bytes. It removes old filters
and external-file entries and updates Length; Data and Decode expose the replacement immediately.
Keep its source/memory open and unchanged. Saves Flate-encode replacements by default, using two bounded
streaming passes to determine Length; set `CompressModifiedStreams = false` to store them unfiltered.
The writer manages Length and replacement filters. Unmodified payloads are always copied as encoded
bytes with their existing Filter and DecodeParms, including unsupported image codecs.

## Limits and failures

| Option | Default | Applies to |
| --- | --- | --- |
| `IsLenient` | false | Explicit syntax and xref recovery |
| `MaxBufferedBytes` | 16 MiB | Tokens, materialized encoded streams, backward-scan buffer, object-stream index arrays |
| `MaxDecodedBytes` | 16 MiB | Each filter output and predictor stage, including intermediate results |
| `MaxDepth` | 128 (maximum 512) | Container/string nesting, resolution depth, page tree, filter count |
| `MaxCrossReferenceSections` | 128 | Prev-chain sections and hybrid supplements |
| `MaxObjects` | 1,000,000 | Retained xref entries, nodes in an object parse, object-stream members, page-tree nodes |
| `StartXrefSearchBytes` | 1 MiB | Backward trailer search; buffering is also constrained by MaxBufferedBytes |
| `MaxRecoveryScanBytes` | 64 MiB | Full-source rebuilding and per-stream end-marker scanning |

Writer options are independent of reader limits:

| Option | Default | Applies to |
| --- | --- | --- |
| `Incremental` | false | Copy original PDF range and append tracked edits |
| `UseCrossReferenceStream` | false | Full-save xref stream; requires PDF 1.5+ |
| `PackObjects` | false | Full-save object streams; requires xref streams, groups at most 100 objects |
| `CompressModifiedStreams` | true | Flate encoding of replaced payloads; false stores raw bytes |
| `MaxBufferedBytes` | 16 MiB | Each object-stream unit and encoded/predicted xref unit |
| `MaxObjects` | 1,000,000 | Full-save Size, retained revision entries and direct nodes per object |
| `MaxDepth` | 128 (maximum 512) | Direct serialization depth |

Classic-table offsets are limited to ten digits; larger offsets require xref streams. Streams,
nonzero-generation objects, the catalog and indirect stream Length values are never packed.
Neither mode implements signing, encryption, linearization, rendering or content-operator editing.
See [save flows and precision rules](DESIGN.md#serialization-and-mutation-decision) for the exact
string/number encodings, bounded buffering and determinism scope.

Lazy unfiltered payload size is not constrained by the memory limits. A sparse 40 GiB fixture
verifies this. Materializing or decoding that payload still requires an explicit sufficient cap
within the runtime array limit. Object caches grow with resolved objects; only one decoded object
stream index is retained at a time. This is bounded per unit, not a process-wide memory quota.

Malformed input uses `ContentFormatException` with `ErrorCode` and an absolute `Position` where
known. No position exists for a rejected non-seekable source. Errors in decoded object-stream
bytes point to the containing stream's source data, since a compressed-byte mapping is unavailable.
See [DESIGN](DESIGN.md#error-model-and-recovery) for classifications, recoveries and unsupported scope.
