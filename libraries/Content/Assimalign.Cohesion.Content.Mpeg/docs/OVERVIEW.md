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
loaded. FastStart uses BMFF's relocation operation and may promote stco to co64. It refuses
fragmented files, unknown/external data references and known unhandled sidx/saio offsets.
Progressive-to-fragmented remuxing is out of scope.

Use a separate output. After changing or saving a document, load a fresh Mp4File over the saved
bytes before querying samples: the old view describes the original source layout. Keep borrowed
sources open and unchanged, and use their cursors sequentially.

## Forward-only streams

Use `BmffReader.Create(input, options, Mp4Profile.CreateRegistry(options))` and
`BmffWriter.Create(output)` for network streams. Header/Payload/Skip avoid media buffering.
Configuration Current materialization requires a seekable backing source; a forward-only caller
consumes its bounded Payload directly.

BMFF currently preserves all children of stsd regardless of registry. LoadDocument re-enters the
same public reader at each retained entry header, retaining the remaining depth budget. This is
the profile's document-binding workaround; raw BMFF streaming of stsd still exposes opaque
sample-entry payloads. The profile does not duplicate the container parser.

## Policy and scope

Mp4ReaderOptions.Strict defaults to true for brand contradictions. Set it false to retain
ContentFormatException diagnostics on the file. ContainerOptions separately controls BMFF
unknown-box handling, buffering, table counts and depth. Structural table/range errors always
throw with a family error code and absolute source position.

Finite fragmented files report IsFragmented, with IsLive false. CMAF checks cover the retained
container constraints described in [DESIGN](DESIGN.md), not full codec or switching-set compliance.
Standalone media segments need their initialization metadata and are not independently loadable
media files.

The [design](DESIGN.md) maps BMFF fields to Media contracts and records current extension gaps.
