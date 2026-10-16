# MP4 profile overview

Content.Mpeg implements ISO/IEC 14496-14 profile behavior on Content.Bmff and Content.Media.
BMFF owns all box framing, generic containers, streaming I/O, document saving and relocation.
Mpeg contributes the retained sample entries, brands and media interpretation. It decodes no
codec bitstreams.

## Inspect tracks and resolve a range

```csharp
using Assimalign.Cohesion.Content.Media;
using Assimalign.Cohesion.Content.Mpeg;

using var source = File.OpenRead("movie.mp4");
using var file = Mp4File.Load(source); // Borrows source; leaveOpen: false transfers ownership.
var video = file.Tracks[0];
var seek = await video.Samples.SeekAsync(new MediaTimestamp(12, 1), cancellationToken);
if (seek is { } range)
{
    // Fetch [range.ByteOffset, range.ByteOffset + range.ByteLength).
    // The decoder also needs the file's initialization metadata and codec configuration.
}
```

Indexes expose source byte coordinates and effective presentation timestamps. They retain native
runs rather than allocating a descriptor for every sample. Time lookup is a floor query; before
a delayed track starts, it returns null. Seeking starts at an eligible preceding sync sample and
covers the complete decode interval. A contiguous range may include interleaved tracks and moof
headers. It is not a standalone playable MP4 or adaptive segment.

Unsupported external data references disable lookup; configuration changes, dependency groups
and auxiliary/encryption metadata disable safe seek. Inspect the capability flags before querying.

## Edit and save

```csharp
using var source = File.OpenRead("movie.mp4");
using var document = Mp4Profile.LoadDocument(source);
Mp4Profile.FastStart(document);
using var output = File.Create("movie-fast.mp4");
await document.SaveAsync(output, cancellationToken);
```

The document preserves unknown boxes, sample-entry reserved bytes, child order and opaque
configuration records. Media stays in borrowed BinarySegments and copies through without being
loaded. FastStart uses BMFF's relocation operation, including sidx segment references and saio
auxiliary addresses, and promotes encodings when necessary. It refuses fragmented files and
unknown/external sample data references; unsupported index versions still fail safe relocation.
Progressive-to-fragmented remuxing is out of scope.

Use a separate output. After changing or saving a document, load a fresh Mp4File over the saved
bytes before querying samples: the old view describes the original source layout. Keep borrowed
sources open and unchanged, and use their cursors sequentially.

## Forward-only streams

Use `BmffReader.Create(input, options, Mp4Profile.CreateRegistry(options))` and
`BmffWriter.Create(output)` for network streams. Header/Payload/Skip avoid media buffering.
Configuration Current materialization requires a seekable backing source; a forward-only caller
consumes its bounded Payload directly.

MP4 registers sample entries with RegisterSampleEntry. BMFF's ReadChildren consumes the stsd
full-box/count prefix and resolves each registered entry directly, retaining the enclosing depth
budget. Unknown entries remain preserved. Configuration boxes derive from BmffBoxLazyLeaf;
their Data aliases the inherited lazy Payload and no Children property remains.

Profiles adding private references can register capture/apply/restore relocation rules with
RegisterRelocation. Save uses the registry supplied at load. See
[BMFF's extension contract](../../Assimalign.Cohesion.Content.Bmff/docs/DESIGN.md#registry-and-profile-extension).

## Policy and scope

Mp4ReaderOptions.Strict defaults to true for brand contradictions. Set it false to retain
ContentFormatException diagnostics on the file. ContainerOptions separately controls BMFF
unknown-box handling, buffering, table counts and depth. Structural table/range errors always
throw with a family error code and absolute source position.

Finite fragmented files report IsFragmented, with IsLive false. CMAF checks cover the retained
container constraints described in [DESIGN](DESIGN.md), not full codec or switching-set compliance.
Standalone media segments need their initialization metadata and are not independently loadable
media files.

The [design](DESIGN.md) maps BMFF fields to Media contracts and documents the extension and relocation contracts.

## Traverse and inspect the native tree

```csharp
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Mpeg;

using var document = Mp4Profile.LoadDocument(source);
var video = document.FirstOrDefault<Avc1SampleEntryBox>();
var configurations = video?.Children<AvcConfigurationBox>();
var descriptions = document.Descendants<SampleDescriptionBox>();
BmffTreeFormatter.Write(document, Console.Out);
```

Formatting the first sample-description subtree in the progressive fixture produces:

```text
stsd @0x216F size=114 children=1 version=0 flags=0x000000 entries=1
  avc1 @0x217F size=98 children=1 dimensions=1920x1080 data-reference=1
    avcC @0x21D5 size=12 lazy-payload=4
```

Sample entries describe dimensions, audio channels/rates and data-reference indices. Configuration
records remain opaque; esds/vpcC say full-box-prefix=lazy because their version/flags have not been
materialized. The description hook never reads codec configuration to make a richer display.

The walker and typed queries are the primary traversal. They work for BMFF, profile and unknown
boxes without extending a visitor. Accept and both visitor interfaces remain for compatibility;
the interfaces' overload set is closed to profiles because BMFF cannot depend on those packages.
A profile box therefore uses Visit(BmffBox), while generic callbacks see its actual type.

Traversal is iterative, detects cycles, and defaults to depth 64. Enter and optional leave
callbacks receive the node, depth and borrowed FourCC path. SkipSubtree skips children but still
calls leave; Stop ends immediately. Copy the path if retaining it, and do not mutate during a walk.
`Children<T>` visits only direct children. `Descendants<T>`, First/FirstOrDefault and FindAll use
preorder; composite queries exclude the receiver, while document queries include top-level boxes.
First and FirstOrDefault also accept BmffBoxType or FourCC; FindAll accepts either raw identity.
First throws InvalidOperationException on absence; FirstOrDefault returns null. Query depth starts
at each direct child: depth zero rejects visited grandchildren; use `Children<T>` for direct children.

BmffTreeFormatter.Write accepts a TextWriter and leaves it open. Format returns LF-terminated
text. Both escape control characters and use invariant numbers. No payload bytes are read: opaque
segments appear only as lengths, even after the source has been closed. A loaded line combines
original header coordinates and total size with current field values. Unloaded boxes are marked;
inspection never calls GetPayloadSize or computes a new output layout.

Offset keeps its existing name: it is the source read position and does not follow editing,
relocation or saving. Limit is the original total byte count, not an end position. Before loading,
constructor-supplied values are retained (zero if omitted). The base derives both from Header once
loaded, and derives IsLeaf/IsComposite from the base type, including empty composites. All package
boxes inherit that state. Existing custom overrides remain supported by the virtual properties.

The inherited debugger display has the form "moov @0x20 size 1,204 [3 children]" or
"mdat @0x0 size 40 GiB (lazy)". A composite proxy exposes its existing Children list. The display
uses header and shape only, without field descriptions, write plans or payload I/O.
