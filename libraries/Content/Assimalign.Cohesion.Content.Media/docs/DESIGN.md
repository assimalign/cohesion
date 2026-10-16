# Assimalign.Cohesion.Content.Media — Design

## Intent and current surface

Media supplies the shared track, sample, and timeline contracts in plan G3 (Stage 3).
`IMediaFile` remains binary content but exposes typed track descriptions instead of requiring
`IComposableContent`: a track's interleaved samples are not a standalone serialized content item.
Profiles may independently implement composition. `MediaTrackKind` replaces file classification;
mixed presentations have several track kinds. The empty `IMediaFileComponent` is removed.

`IMediaTrack`, `IMediaVideoTrack`, and `IMediaAudioTrack` expose native codec identifiers,
lazy `BinarySegment` private data, language, timing, selection flags, and container-declared
geometry/audio properties. `IMediaSampleIndex` and its guided `MediaSampleIndex` base support
streaming plus explicitly optional index/time/seek lookup, without eager table expansion.
`MediaSample` records absolute source bytes, decode/source order, sync status, optional DTS,
and effective presentation time. `MediaSeekResult` includes sync and target samples plus a
contiguous fetch range, including dependencies and framing as resolved by the profile.

`MediaTimestamp` is signed Int64 ticks over a positive Int64 timescale. Int128 intermediates
avoid overflow during comparison/rescaling; exact arithmetic reduces before checking bounds.
Default is zero seconds. Exact conversion rejects fractional ticks; rounded conversion accepts
all `MidpointRounding` policies. TimeSpan is for display only.

ISO/IEC 14496-12 profiles project `trak`, sample tables, edits, and fragments; RFC 9559 profiles
project TrackEntry, blocks/laces, and cues. Missing Matroska DTS stays null. PresentationOffset
is the effective PTS minus DTS, including any timeline shift, rather than a raw composition offset.
Unknown duration is independent of live state. BMFF fragmentation remains a profile detail.

The current dependency direction keeps binary operations below media abstractions:

```mermaid
flowchart LR
    Bmff["Content.Bmff"] --> Media["Content.Media"]
    Media --> Binary["Content.Binary"]
    Binary --> Root["Content"]
```

BMFF references Media, Media references Binary, and Binary references the root Content package.
Exact reads and fixed-point values live in Binary. Media's former ReadBytes ignored partial reads;
its StreamExtensions and FPoint have been removed in favor of the shared primitives.

## Lifecycle, errors, and AOT

Track/index views and lazy codec segments borrow the file's source and lifetime; callers must
not change its layout during use. The profile controls live snapshots and refresh. Concurrent
operations on a shared stream are not implied. The index base handles cancellation and optional
capabilities but supplies no costly enumeration fallback. Profiles must map malformed data to
ContentFormatException with an absolute position. Value-object construction rejects invalid caller
arguments and unrepresentable arithmetic through resource-backed exceptions. No new error root
or container reader is introduced.

The inherited IsAotCompatible posture remains: no reflection or runtime-generated code.
Playback, codecs, transcoding, transport, and service orchestration remain outside this layer.
