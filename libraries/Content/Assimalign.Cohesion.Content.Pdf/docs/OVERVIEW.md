# PDF package overview

`Assimalign.Cohesion.Content.Pdf` reads PDF structure and supplies editable values for the
writer planned in Stage 3. The public reader implements the root `IContentReader<PdfDocument>`
contract. `Content.Pdf` references `Content.Binary`, which supplies lazy `BinarySegment` ranges,
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

## Read stream bytes

`PdfStream.Dictionary` preserves the stream's metadata. `Data` is the original encoded
`BinarySegment`; opening/copying it does not invoke filters. `Decode()` returns bounded decoded
memory on demand and reflects the current filter dictionary. It supports Flate, ASCIIHex,
ASCII85, LZW, RunLength, TIFF prediction and all PNG row predictors. A decode call is not cached.
Image filters remain available as encoded bytes and return `Unsupported` when decoding is requested.
All strings and stream bytes belonging to an encrypted document return `Unsupported` on access,
including trailer strings; cross-reference information remains loadable.

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

Lazy unfiltered payload size is not constrained by the memory limits. A sparse 40 GiB fixture
verifies this. Materializing or decoding that payload still requires an explicit sufficient cap
within the runtime array limit. Object caches grow with resolved objects; only one decoded object
stream index is retained at a time. This is bounded per unit, not a process-wide memory quota.

Malformed input uses `ContentFormatException` with `ErrorCode` and an absolute `Position` where
known. No position exists for a rejected non-seekable source. Errors in decoded object-stream
bytes point to the containing stream's source data, since a compressed-byte mapping is unavailable.
See [DESIGN](DESIGN.md#error-model-and-recovery) for classifications, recoveries and unsupported scope.
