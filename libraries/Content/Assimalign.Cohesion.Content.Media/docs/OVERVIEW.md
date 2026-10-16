# Assimalign.Cohesion.Content.Media — Overview

Content.Media describes encoded media independently of its container. It provides presentation and
track contracts, exact rational timestamps, lazy sample indexes, and seek results expressed as source
byte ranges. Content.Mpeg and Content.Mkv implement this vocabulary over BMFF and Matroska.

Its only direct dependency is Content.Binary. Codec identifiers remain native strings; codec private
data remains an opaque, preferably lazy BinarySegment. There is no container reader, codec parser,
decoder, HTTP server, or playback engine in this package.

| Surface | Purpose |
| --- | --- |
| IMediaFile | Binary content, discovered tracks, optional duration, explicit live state |
| IMediaTrack; IMediaVideoTrack; IMediaAudioTrack | Identity, kind, codec, language, timing, selection, per-kind metadata |
| MediaTimestamp | Exact signed ticks/timescale arithmetic, comparison, rounding, display conversion |
| IMediaSampleIndex; MediaSampleIndex | Streaming plus optional lookup by index, time, or seek |
| MediaSample | Track/index identity, optional DTS, effective PTS/offset, payload range, sync flag |
| MediaSeekResult | Sync/target descriptors and a contiguous source range covering decode dependencies |

## Using the model

A timestamp is a value divided by ticks per second. A 30000/1001 frame rate advances by 1001 ticks
at timescale 30000. Keep exact values for arithmetic and use TimeSpan only for display.

```csharp
var frameDuration = new MediaTimestamp(1001, 30000);
var at90Khz = frameDuration.RescaleExact(90000); // Value = 3003
var at48Khz = frameDuration.Rescale(48000, MidpointRounding.ToEven); // Value = 1602
var display = frameDuration.ToTimeSpan();
```

For range serving, select a track and query presentation time. Here file is an IMediaFile and
cancellationToken is supplied by the caller:

```csharp
var track = file.Tracks[0]; // Application selects the desired track.
var requested = new MediaTimestamp(5025, 1); // 01:23:45
if (track.Samples.CanSeek &&
    await track.Samples.SeekAsync(requested, cancellationToken) is { } seek &&
    seek.ByteLength > 0)
{
    long firstByte = seek.ByteOffset;
    long lastByte = firstByte + seek.ByteLength - 1; // HTTP inclusive end.
    // Fetch [firstByte, lastByte] from the unchanged source.
}
```

The range assumes initialization metadata is available and may contain interleaved bytes.
A profile may refuse a particular seek if it cannot prove the range is safe. Null means no target
or preceding sync sample. This does not create a standalone playable segment.

For forward-only sources, use ReadSamplesAsync(cancellationToken) and process one descriptor at a time.
Check CanLookupByIndex/CanLookupByTime before random queries. Count and Duration can be unknown.
Presentation timestamps can be out of decode order; Matroska-like sources can have no explicit DTS.
IsLive and unknown duration are independent.

## Ownership and migration

Tracks, indexes, and lazy codec data borrow the file's source and lifetime. Keep it open and do not
change its byte layout during use; after edits/save obtain a fresh view. Streams opened through
IContent or BinarySegment are caller-owned. Concurrent reads of one shared source are not implied.

MediaTrackKind replaces MediaFileKind; mixed presentations are classified track by track.
IMediaFile retains IBinaryFile but no longer requires IComposableContent or a file-level Kind.
The empty IMediaFileComponent was removed. Profiles can independently implement content composition.
BMFF fragmentation stays in Content.Mpeg rather than becoming a shared flag.

See [DESIGN.md](DESIGN.md) for every member's BMFF/Matroska mapping, exact rounding rules,
the lazy-index contract, range semantics, extension points, and non-goals.
