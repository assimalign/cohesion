# Assimalign.Cohesion.Content.Bmff

BMFF provides ISO base media file boxes, forward-only reading/writing, and an editable document
beneath MP4 and HEIF profiles. It references Content.Media and uses Content.Binary for bounded
streams, exact reads, FourCC, fixed-point numbers and lazy BinarySegment payloads.

## Inspect a large source

```csharp
using var reader = BmffReader.Create(input);
while (reader.Read())
{
    if (reader.Header.BoxType == BmffBoxType.Movie)
    {
        using var children = reader.ReadChildren();
        while (children.Read())
        {
            Inspect(children.Header);
        }
    }
}
```

Reading a header never materializes media. On a seekable source, advancing skips media by seeking;
on a network stream, advancing drains unread bytes through a fixed transfer buffer. Payload exposes
the current bounded stream. Consume it before advancing. Current materializes supported metadata;
media and opaque Current nodes need a seekable source for their lazy segments.

## Traverse and inspect a tree

```csharp
using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

using var document = BmffDocument.Load(input);
var movie = document.First<MovieBox>();
var tracks = movie.Children<TrackBox>();
var headers = document.Descendants<TrackHeaderBox>();
var media = document.FindAll(BmffBoxType.MediaData);
var unknown = document.FirstOrDefault(FourCC.Parse("zzzz"));
BmffTreeFormatter.Write(document, Console.Out);

BmffBoxWalker.Walk(document, (box, depth, path) =>
{
    Console.WriteLine($"{depth}: {string.Join('/', path)}");
    return box.BoxType == BmffBoxType.UserData
        ? BmffWalkAction.SkipSubtree : BmffWalkAction.Continue;
});
```

An excerpt from the progressive fixture's movie subtree:

```text
moov @0x201A size=982 children=3
  mvhd @0x2022 size=108 version=0 flags=0x000000 timescale=1000 duration=4000 next-track-id=3
  trak @0x208E size=459 children=2
    tkhd @0x2096 size=92 version=0 flags=0x000003 track-id=1 duration=4000 dimensions=0x0
```

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

## Edit and save

```csharp
using var document = BmffDocument.Load(input);
var fileType = document.First<FileTypeBox>();
fileType.CompatibleBrands = new[] { FourCC.Parse("isom").Value };

var movieIndex = -1;
for (var index = 0; index < document.Boxes.Count; index++)
{
    if (document.Boxes[index] is MovieBox)
    {
        movieIndex = index;
        break;
    }
}
if (movieIndex > 1)
{
    var movie = document.Boxes[movieIndex];
    document.Boxes.RemoveAt(movieIndex);
    document.Boxes.Insert(1, movie);
}

// Optional explicit planning; SaveAsync also relocates supported references.
document.RelocateMediaOffsets();
await document.SaveAsync(output, cancellationToken);
```

The example assumes an ftyp-first progressive file with its moov after mdat. Save recomputes sizes
and relocates in-file chunk/item/fragment, sidx and saio references. It promotes stco to co64 when
needed and plans
again until the layout stabilizes. Repeated saves do not apply the shift twice. The exact supported
references and fragment-base restrictions are in [DESIGN](DESIGN.md#relocation-and-mutation).

Load requires seekable input and borrows it by default; leaveOpen: false transfers disposal to the
document. Keep that source open and byte-stable through save. Use a separate output. Media, codec
configuration and unknown payloads copy through without loading them. Calls on shared sources,
readers, writers and models are sequential. A new document's first save establishes its layout;
its caller supplies initial offsets.

## Work with fields

Every standard box remains its own sealed model. Containers expose ordered Children. For example,
SampleDescriptionBox and DataReferenceBox expose EntryCount through that collection. stsd entries
resolve through RegisterSampleEntry factories in the active registry. Ordinary box registrations
do not apply in that context; unregistered entries remain lazy and opaque, including in strict mode.
The MP4 profile supplies the codec entry grammars.

MovieHeaderBox, TrackHeaderBox and MediaHeaderBox own their timing fields. CreationTime and
ModificationTime are raw unsigned seconds since 1904-01-01 UTC. Version zero selects 32-bit time
fields and version one selects 64-bit fields; callers choose the version explicitly. Fixed-point
rate/volume/dimensions preserve their raw bits through Content.Binary types.

Large sample tables use primitive arrays or arrays of nested Entry structs. For ctts/trun,
CompositionTimeOffset/SampleOffset uses long to represent unsigned v0 and signed v1 values.
For tfhd and trun, set both Flags and the corresponding nullable optional fields. Fields outside
the selected version/width or inconsistent with flags fail with ContentFormatException.

FileTypeBox.CompatibleBrands replaces the old CompatableBrands spelling without an alias.
TrackBox's misplaced header/rate properties were removed; use the owning mvhd/tkhd/mdhd box.
MovieHeaderBox now uses the inherited byte Version and mutable raw/fixed-point properties.

## Stream output or extend a profile

```csharp
using var writer = BmffWriter.Create(output);
writer.BeginContainer(BmffBoxType.MovieFragment, payloadSize: knownChildBytes);
writer.Write(fragmentChild);
writer.EndContainer();
```

Supply a known payload size to stream directly, or omit it for bounded buffering. WritePayload
copies raw segments. Both modes support non-seekable outputs.

```csharp
var registry = BmffBoxRegistry.CreateBuilder()
    .Register(profileType, header => new ProfileBox(header))
    .Build();
using var reader = BmffReader.Create(input, new BmffReaderOptions(), registry);
```

A profile supplies Read, Write and GetPayloadSize on a box model. Factories receive validated
headers; registry snapshots are immutable. Composite extension bytes precede ordered children,
and their size is included in relocation. New standard models use the existing Visit(BmffBox)
fallback so visitor interfaces remain unchanged.

## The three profile seams

| Need | Profile uses | Contract |
| --- | --- | --- |
| Opaque configuration or item data | Derive a sealed box from BmffBoxLazyLeaf and register its factory | Payload is a borrowed BinarySegment. ReadPayloadBytes is the protected, capped materialization path. Typed accessors belong to the subclass. |
| Typed stsd entries | RegisterSampleEntry(type, factory) | The same bounded reader materializes entries and descendants with the enclosing depth budget. Unknown entries remain preserved. |
| Private offset-bearing boxes | RegisterRelocation(type, capture) | Capture returns apply/restore callbacks; apply uses IBmffRelocationContext to map checkpoint offsets and box positions. Failed planning restores fields. |

Load the document with the completed registry so its relocation rules also apply on Save.
A rule captures its original fields once and uses those same values on every planning pass.
It must restore every field it changes. This includes version/width promotion if the profile
format needs it. A registered rule replaces the built-in rule for its FourCC.

sidx relocates the end-relative first offset and referenced spans, including references to other
indexes. saio relocates absolute sample-table addresses and supported tfhd-relative fragment
addresses, including auxiliary bytes in opaque senc boxes. Version-zero fields promote when
necessary. These two field implementations are internal; inspect their generic box identity
or emitted bytes. Existing public visitor interfaces are unchanged.

Profiles compacting a payload call RelocateMediaOffsets(mapPayloadOffset, outputOffset) after
replacing it. The pure mapper translates payload-relative byte addresses and exclusive ends;
a negative result rejects a deleted target. HEIF uses this to erase removed metadata while
relocating retained iloc extents and other supported references. See the complete
[relocation contract](DESIGN.md#relocation-and-mutation) for rollback and unsupported bases.

## Limits and scope

Defaults are a 16 MiB buffered/decoded leaf budget, 1,000,000 table entries and depth 64.
Counts and payload bounds are validated before allocations. Unknown/unimplemented types and
unsupported versions are preserved in lenient mode; strict mode rejects them, except deliberately
opaque stsd sample entries. Malformed supported fields report ContentFormatException with a code
and absolute position.

The strategic set covers movie, track/media, sample-table, fragment, item metadata and protection
signalling fields. The [design table](DESIGN.md#field-encodings-and-validation) records versions and
flags, the [preserved set](DESIGN.md#strategic-field-models) names deferred models, and the
[byte-preservation rule](DESIGN.md#parsed-byte-preservation) defines round-trip behavior.
Codec interpretation, decryption and sample indexing remain outside this layer. Profile-specific
references require explicit registered relocation rules.

## Stage 3 public surface inventory

Each line names one added, removed or changed public declaration or behavior. Field properties
are mutable unless described as a decoded/count getter. Existing visitor interfaces are unchanged.

<details>
<summary>Complete per-member inventory</summary>

```text
BmffReaderOptions.MaxTableEntryCount : int — added, default 1,000,000.
BmffDocument.RelocateMediaOffsets(long outputOffset = 0) — added.
BmffDocument.Load — records the original relocation layout.
BmffDocument.LoadAsync — records the original relocation layout.
BmffDocument.Save — relocates supported references before writing.
BmffDocument.SaveAsync — relocates supported references before writing.
BmffBoxComposite.Read — parses counted children and preserves their suffix.
BmffBoxComposite.Write — emits preserved suffix bytes.
BmffBoxComposite.GetPayloadSize — includes preserved suffix bytes.
BmffBoxRegistry.Default — activates strategic parsers and registers new box types.
BmffBoxType.TrackFragmentDecodeTime — added tfdt value.
BmffBoxType.DataEntryUrl — added url value.
BmffBoxType.DataEntryUrn — added urn value.
BmffBoxType.ItemInfoEntry — added infe value.
FileTypeBox.CompatableBrands — removed; renamed without an alias.
FileTypeBox.CompatibleBrands : uint[] — added replacement name.
MovieHeaderBox.Version : BmffVersion — removed hiding property; inherited mutable byte Version now applies.
MovieHeaderBox(long size, long offset) — now defaults to version 0, epoch times, rate/volume 1, timescale 1000.
TrackBox.Version — removed misplaced field.
TrackBox.Flags — removed misplaced field.
TrackBox.TimeScale — removed misplaced field.
TrackBox.Duration — removed misplaced field.
TrackBox.Rate — removed misplaced field.
TrackBox.Volume — removed misplaced field.
TrackBox.Reserved — removed misplaced field.
TrackBox.Matrix — removed misplaced field.
TrackBox.PreDefined — removed misplaced field.
TrackBox.NextTrackID — removed misplaced field.
BinaryXmlBox.Data : byte[] — added.
BinaryXmlBox.Read(BmffStream stream) — implemented.
BinaryXmlBox.Write(BmffStream stream) — implemented.
BinaryXmlBox.GetPayloadSize() — added.
ChunkOffsetBox.Entries : uint[] — changed.
ChunkOffsetBox.Read(BmffStream stream) — implemented.
ChunkOffsetBox.Write(BmffStream stream) — implemented.
ChunkOffsetBox.GetPayloadSize() — added.
ChunkOffset64BitBox.Entries : ulong[] — added.
ChunkOffset64BitBox.Read(BmffStream stream) — implemented.
ChunkOffset64BitBox.Write(BmffStream stream) — implemented.
ChunkOffset64BitBox.GetPayloadSize() — added.
CopyrightBox.PackedLanguage : ushort — added.
CopyrightBox.Language : string — added.
CopyrightBox.Notice : string — added.
CopyrightBox.Read(BmffStream stream) — implemented.
CopyrightBox.Write(BmffStream stream) — implemented.
CopyrightBox.GetPayloadSize() — added.
DataEntryUrlBox — added sealed box type.
DataEntryUrlBox.IsLeaf : bool — added.
DataEntryUrlBox.IsComposite : bool — added.
DataEntryUrlBox.Limit : long — added.
DataEntryUrlBox.Offset : long — added.
DataEntryUrlBox.BoxType : BmffBoxType — added.
DataEntryUrlBox.Location : string — added.
DataEntryUrlBox.Read(BmffStream stream) — added.
DataEntryUrlBox.Write(BmffStream stream) — added.
DataEntryUrlBox.GetPayloadSize() — added.
DataEntryUrlBox.Accept<T>(IBmffBoxVisitor<T> visitor) — added.
DataEntryUrlBox.Accept(IBmffBoxVisitor visitor) — added.
DataEntryUrlBox() — added parameterless constructor.
DataEntryUrnBox — added sealed box type.
DataEntryUrnBox.IsLeaf : bool — added.
DataEntryUrnBox.IsComposite : bool — added.
DataEntryUrnBox.Limit : long — added.
DataEntryUrnBox.Offset : long — added.
DataEntryUrnBox.BoxType : BmffBoxType — added.
DataEntryUrnBox.Name : string — added.
DataEntryUrnBox.Location : string — added.
DataEntryUrnBox.Read(BmffStream stream) — added.
DataEntryUrnBox.Write(BmffStream stream) — added.
DataEntryUrnBox.GetPayloadSize() — added.
DataEntryUrnBox.Accept<T>(IBmffBoxVisitor<T> visitor) — added.
DataEntryUrnBox.Accept(IBmffBoxVisitor visitor) — added.
DataEntryUrnBox() — added parameterless constructor.
DataReferenceBox — base changed from BmffFullBox to BmffBoxComposite.
DataReferenceBox.Children — inherited composite behavior.
DataReferenceBox.IsLeaf — inherited composite behavior.
DataReferenceBox.IsComposite — inherited composite behavior.
DataReferenceBox.Read(BmffStream) — inherited composite behavior.
DataReferenceBox.Write(BmffStream) — inherited composite behavior.
DataReferenceBox.GetPayloadSize() — inherited composite behavior.
DataReferenceBox.Version : byte — changed.
DataReferenceBox.Flags : uint — changed.
DataReferenceBox.EntryCount : int — added.
DisposableSampleBox.Entry — added nested value type.
DisposableSampleBox.Entry.IsLeading : byte — added.
DisposableSampleBox.Entry.DependsOn : byte — added.
DisposableSampleBox.Entry.IsDependedOn : byte — added.
DisposableSampleBox.Entry.HasRedundancy : byte — added.
DisposableSampleBox.Entries : Entry[] — added.
DisposableSampleBox.Read(BmffStream stream) — implemented.
DisposableSampleBox.Write(BmffStream stream) — implemented.
DisposableSampleBox.GetPayloadSize() — added.
EditListBox.Entry — added nested value type.
EditListBox.Entry.SegmentDuration : ulong — added.
EditListBox.Entry.MediaTime : long — added.
EditListBox.Entry.MediaRateInteger : short — added.
EditListBox.Entry.MediaRateFraction : short — added.
EditListBox.Entries : Entry[] — added.
EditListBox.Read(BmffStream stream) — implemented.
EditListBox.Write(BmffStream stream) — implemented.
EditListBox.GetPayloadSize() — added.
HandlerBox.PreDefined : uint — added.
HandlerBox.HandlerType : FourCC — added.
HandlerBox.Reserved : byte[] — added.
HandlerBox.Name : string — added.
HandlerBox.Read(BmffStream stream) — implemented.
HandlerBox.Write(BmffStream stream) — implemented.
HandlerBox.GetPayloadSize() — added.
HintMediaHeaderBox.MaxPduSize : ushort — added.
HintMediaHeaderBox.AveragePduSize : ushort — added.
HintMediaHeaderBox.MaxBitrate : uint — added.
HintMediaHeaderBox.AverageBitrate : uint — added.
HintMediaHeaderBox.Reserved : uint — added.
HintMediaHeaderBox.Read(BmffStream stream) — implemented.
HintMediaHeaderBox.Write(BmffStream stream) — implemented.
HintMediaHeaderBox.GetPayloadSize() — added.
ItemInfoBox — base changed from BmffFullBox to BmffBoxComposite.
ItemInfoBox.Children — inherited composite behavior.
ItemInfoBox.IsLeaf — inherited composite behavior.
ItemInfoBox.IsComposite — inherited composite behavior.
ItemInfoBox.Read(BmffStream) — inherited composite behavior.
ItemInfoBox.Write(BmffStream) — inherited composite behavior.
ItemInfoBox.GetPayloadSize() — inherited composite behavior.
ItemInfoBox.Version : byte — changed.
ItemInfoBox.Flags : uint — changed.
ItemInfoBox.EntryCount : int — added.
ItemInfoEntryBox — added sealed box type.
ItemInfoEntryBox.IsLeaf : bool — added.
ItemInfoEntryBox.IsComposite : bool — added.
ItemInfoEntryBox.Limit : long — added.
ItemInfoEntryBox.Offset : long — added.
ItemInfoEntryBox.BoxType : BmffBoxType — added.
ItemInfoEntryBox.ItemId : uint — added.
ItemInfoEntryBox.ItemProtectionIndex : ushort — added.
ItemInfoEntryBox.ItemType : FourCC — added.
ItemInfoEntryBox.ItemName : string — added.
ItemInfoEntryBox.ContentType : string — added.
ItemInfoEntryBox.ContentEncoding : string — added.
ItemInfoEntryBox.HasContentEncoding : bool — added.
ItemInfoEntryBox.ItemUriType : string — added.
ItemInfoEntryBox.Read(BmffStream stream) — added.
ItemInfoEntryBox.Write(BmffStream stream) — added.
ItemInfoEntryBox.GetPayloadSize() — added.
ItemInfoEntryBox.Accept<T>(IBmffBoxVisitor<T> visitor) — added.
ItemInfoEntryBox.Accept(IBmffBoxVisitor visitor) — added.
ItemInfoEntryBox() — added parameterless constructor.
ItemLocationBox.OffsetSize : byte — added.
ItemLocationBox.LengthSize : byte — added.
ItemLocationBox.BaseOffsetSize : byte — added.
ItemLocationBox.IndexSize : byte — added.
ItemLocationBox.Extent — added nested value type.
ItemLocationBox.Extent.Index : ulong — added.
ItemLocationBox.Extent.Offset : ulong — added.
ItemLocationBox.Extent.Length : ulong — added.
ItemLocationBox.Entry — added nested value type.
ItemLocationBox.Entry.ItemId : uint — added.
ItemLocationBox.Entry.ConstructionMethod : ushort — added.
ItemLocationBox.Entry.Reserved : ushort — added.
ItemLocationBox.Entry.DataReferenceIndex : ushort — added.
ItemLocationBox.Entry.BaseOffset : ulong — added.
ItemLocationBox.Entry.Extents : Extent[] — added.
ItemLocationBox.Entries : Entry[] — added.
ItemLocationBox.Read(BmffStream stream) — implemented.
ItemLocationBox.Write(BmffStream stream) — implemented.
ItemLocationBox.GetPayloadSize() — added.
MediaHeaderBox.CreationTime : ulong — added.
MediaHeaderBox.ModificationTime : ulong — added.
MediaHeaderBox.TimeScale : uint — added.
MediaHeaderBox.Duration : ulong — added.
MediaHeaderBox.PackedLanguage : ushort — added.
MediaHeaderBox.Language : string — added.
MediaHeaderBox.PreDefined : ushort — added.
MediaHeaderBox.Read(BmffStream stream) — implemented.
MediaHeaderBox.Write(BmffStream stream) — implemented.
MediaHeaderBox.GetPayloadSize() — added.
MovieExtensionHeaderBox.FragmentDuration : ulong — added.
MovieExtensionHeaderBox.Read(BmffStream stream) — implemented.
MovieExtensionHeaderBox.Write(BmffStream stream) — implemented.
MovieExtensionHeaderBox.GetPayloadSize() — added.
MovieFragmentHeaderBox.SequenceNumber : uint — added.
MovieFragmentHeaderBox.Read(BmffStream stream) — implemented.
MovieFragmentHeaderBox.Write(BmffStream stream) — implemented.
MovieFragmentHeaderBox.GetPayloadSize() — added.
MovieFragmentRandomAccessOffsetBox.MfraSize : uint — added.
MovieFragmentRandomAccessOffsetBox.Read(BmffStream stream) — implemented.
MovieFragmentRandomAccessOffsetBox.Write(BmffStream stream) — implemented.
MovieFragmentRandomAccessOffsetBox.GetPayloadSize() — added.
MovieHeaderBox.CreationTime : ulong — changed.
MovieHeaderBox.ModificationTime : ulong — changed.
MovieHeaderBox.TimeScale : uint — changed.
MovieHeaderBox.Duration : ulong — changed.
MovieHeaderBox.Rate : FixedPoint16x16 — changed.
MovieHeaderBox.Volume : FixedPoint8x8 — changed.
MovieHeaderBox.Reserved : byte[] — changed.
MovieHeaderBox.Matrix : int[] — changed.
MovieHeaderBox.PreDefined : byte[] — changed.
MovieHeaderBox.NextTrackId : uint — changed.
MovieHeaderBox.Read(BmffStream stream) — implemented.
MovieHeaderBox.Write(BmffStream stream) — implemented.
MovieHeaderBox.GetPayloadSize() — added.
NullMediaHeaderBox.Read(BmffStream stream) — implemented.
NullMediaHeaderBox.Write(BmffStream stream) — implemented.
NullMediaHeaderBox.GetPayloadSize() — added.
OriginalFormatBox.DataFormat : FourCC — added.
OriginalFormatBox.Read(BmffStream stream) — implemented.
OriginalFormatBox.Write(BmffStream stream) — implemented.
OriginalFormatBox.GetPayloadSize() — added.
PrimaryItemReferenceBox.ItemId : uint — added.
PrimaryItemReferenceBox.Read(BmffStream stream) — implemented.
PrimaryItemReferenceBox.Write(BmffStream stream) — implemented.
PrimaryItemReferenceBox.GetPayloadSize() — added.
ProgressiveDownloadInfoBox.Entry — added nested value type.
ProgressiveDownloadInfoBox.Entry.Rate : uint — added.
ProgressiveDownloadInfoBox.Entry.InitialDelay : uint — added.
ProgressiveDownloadInfoBox.Entries : Entry[] — added.
ProgressiveDownloadInfoBox.Read(BmffStream stream) — implemented.
ProgressiveDownloadInfoBox.Write(BmffStream stream) — implemented.
ProgressiveDownloadInfoBox.GetPayloadSize() — added.
SampleCompositionTimeBox.Entry — added nested value type.
SampleCompositionTimeBox.Entry.SampleCount : uint — added.
SampleCompositionTimeBox.Entry.SampleOffset : long — added.
SampleCompositionTimeBox.Entries : Entry[] — added.
SampleCompositionTimeBox.Read(BmffStream stream) — implemented.
SampleCompositionTimeBox.Write(BmffStream stream) — implemented.
SampleCompositionTimeBox.GetPayloadSize() — added.
SampleDecodingTimeBox.Entry — added nested value type.
SampleDecodingTimeBox.Entry.SampleCount : uint — added.
SampleDecodingTimeBox.Entry.SampleDelta : uint — added.
SampleDecodingTimeBox.Entries : Entry[] — added.
SampleDecodingTimeBox.Read(BmffStream stream) — implemented.
SampleDecodingTimeBox.Write(BmffStream stream) — implemented.
SampleDecodingTimeBox.GetPayloadSize() — added.
SampleDescriptionBox — base changed from BmffFullBox to BmffBoxComposite.
SampleDescriptionBox.Children — inherited composite behavior.
SampleDescriptionBox.IsLeaf — inherited composite behavior.
SampleDescriptionBox.IsComposite — inherited composite behavior.
SampleDescriptionBox.Read(BmffStream) — inherited composite behavior.
SampleDescriptionBox.Write(BmffStream) — inherited composite behavior.
SampleDescriptionBox.GetPayloadSize() — inherited composite behavior.
SampleDescriptionBox.Version : byte — changed.
SampleDescriptionBox.Flags : uint — changed.
SampleDescriptionBox.EntryCount : int — added.
SampleGroupDescriptionBox.GroupingType : FourCC — added.
SampleGroupDescriptionBox.DefaultLength : uint — added.
SampleGroupDescriptionBox.DefaultSampleDescriptionIndex : uint — added.
SampleGroupDescriptionBox.Entries : byte[][] — added.
SampleGroupDescriptionBox.Read(BmffStream stream) — implemented.
SampleGroupDescriptionBox.Write(BmffStream stream) — implemented.
SampleGroupDescriptionBox.GetPayloadSize() — added.
SampleSizeBox.SampleSize : uint — added.
SampleSizeBox.SampleCount : uint — added.
SampleSizeBox.Entries : uint[] — added.
SampleSizeBox.Read(BmffStream stream) — implemented.
SampleSizeBox.Write(BmffStream stream) — implemented.
SampleSizeBox.GetPayloadSize() — added.
CompactSampleSizeBox.Reserved : uint — added.
CompactSampleSizeBox.FieldSize : byte — added.
CompactSampleSizeBox.Entries : ushort[] — added.
CompactSampleSizeBox.Read(BmffStream stream) — implemented.
CompactSampleSizeBox.Write(BmffStream stream) — implemented.
CompactSampleSizeBox.GetPayloadSize() — added.
SampleSubInfoBox.Subsample — added nested value type.
SampleSubInfoBox.Subsample.Size : uint — added.
SampleSubInfoBox.Subsample.Priority : byte — added.
SampleSubInfoBox.Subsample.Discardable : byte — added.
SampleSubInfoBox.Subsample.CodecSpecificParameters : uint — added.
SampleSubInfoBox.Entry — added nested value type.
SampleSubInfoBox.Entry.SampleDelta : uint — added.
SampleSubInfoBox.Entry.Subsamples : Subsample[] — added.
SampleSubInfoBox.Entries : Entry[] — added.
SampleSubInfoBox.Read(BmffStream stream) — implemented.
SampleSubInfoBox.Write(BmffStream stream) — implemented.
SampleSubInfoBox.GetPayloadSize() — added.
SampleToChunkBox.Entry — added nested value type.
SampleToChunkBox.Entry.FirstChunk : uint — added.
SampleToChunkBox.Entry.SamplesPerChunk : uint — added.
SampleToChunkBox.Entry.SampleDescriptionIndex : uint — added.
SampleToChunkBox.Entries : Entry[] — added.
SampleToChunkBox.Read(BmffStream stream) — implemented.
SampleToChunkBox.Write(BmffStream stream) — implemented.
SampleToChunkBox.GetPayloadSize() — added.
SampleToGroupBox.GroupingType : FourCC — added.
SampleToGroupBox.GroupingTypeParameter : uint — added.
SampleToGroupBox.Entry — added nested value type.
SampleToGroupBox.Entry.SampleCount : uint — added.
SampleToGroupBox.Entry.GroupDescriptionIndex : uint — added.
SampleToGroupBox.Entries : Entry[] — added.
SampleToGroupBox.Read(BmffStream stream) — implemented.
SampleToGroupBox.Write(BmffStream stream) — implemented.
SampleToGroupBox.GetPayloadSize() — added.
SchemeTypeBox.SchemeType : FourCC — added.
SchemeTypeBox.SchemeVersion : uint — added.
SchemeTypeBox.SchemeUri : string — added.
SchemeTypeBox.Read(BmffStream stream) — implemented.
SchemeTypeBox.Write(BmffStream stream) — implemented.
SchemeTypeBox.GetPayloadSize() — added.
SoundMediaHeaderBox.Balance : FixedPoint8x8 — added.
SoundMediaHeaderBox.Reserved : ushort — added.
SoundMediaHeaderBox.Read(BmffStream stream) — implemented.
SoundMediaHeaderBox.Write(BmffStream stream) — implemented.
SoundMediaHeaderBox.GetPayloadSize() — added.
SyncSampleTableBox.Entries : uint[] — added.
SyncSampleTableBox.Read(BmffStream stream) — implemented.
SyncSampleTableBox.Write(BmffStream stream) — implemented.
SyncSampleTableBox.GetPayloadSize() — added.
TrackExtensionBox.TrackId : uint — added.
TrackExtensionBox.DefaultSampleDescriptionIndex : uint — added.
TrackExtensionBox.DefaultSampleDuration : uint — added.
TrackExtensionBox.DefaultSampleSize : uint — added.
TrackExtensionBox.DefaultSampleFlags : uint — added.
TrackExtensionBox.Read(BmffStream stream) — implemented.
TrackExtensionBox.Write(BmffStream stream) — implemented.
TrackExtensionBox.GetPayloadSize() — added.
TrackFragmentDecodeTimeBox — added sealed box type.
TrackFragmentDecodeTimeBox.IsLeaf : bool — added.
TrackFragmentDecodeTimeBox.IsComposite : bool — added.
TrackFragmentDecodeTimeBox.Limit : long — added.
TrackFragmentDecodeTimeBox.Offset : long — added.
TrackFragmentDecodeTimeBox.BoxType : BmffBoxType — added.
TrackFragmentDecodeTimeBox.BaseMediaDecodeTime : ulong — added.
TrackFragmentDecodeTimeBox.Read(BmffStream stream) — added.
TrackFragmentDecodeTimeBox.Write(BmffStream stream) — added.
TrackFragmentDecodeTimeBox.GetPayloadSize() — added.
TrackFragmentDecodeTimeBox.Accept<T>(IBmffBoxVisitor<T> visitor) — added.
TrackFragmentDecodeTimeBox.Accept(IBmffBoxVisitor visitor) — added.
TrackFragmentDecodeTimeBox() — added parameterless constructor.
TrackFragmentHeaderBox.TrackId : uint — added.
TrackFragmentHeaderBox.BaseDataOffset : ulong? — added.
TrackFragmentHeaderBox.SampleDescriptionIndex : uint? — added.
TrackFragmentHeaderBox.DefaultSampleDuration : uint? — added.
TrackFragmentHeaderBox.DefaultSampleSize : uint? — added.
TrackFragmentHeaderBox.DefaultSampleFlags : uint? — added.
TrackFragmentHeaderBox.Read(BmffStream stream) — implemented.
TrackFragmentHeaderBox.Write(BmffStream stream) — implemented.
TrackFragmentHeaderBox.GetPayloadSize() — added.
TrackFragmentRandomAccessBox.TrackId : uint — added.
TrackFragmentRandomAccessBox.TrafNumberSize : byte — added.
TrackFragmentRandomAccessBox.TrunNumberSize : byte — added.
TrackFragmentRandomAccessBox.SampleNumberSize : byte — added.
TrackFragmentRandomAccessBox.Reserved : uint — added.
TrackFragmentRandomAccessBox.Entry — added nested value type.
TrackFragmentRandomAccessBox.Entry.Time : ulong — added.
TrackFragmentRandomAccessBox.Entry.MoofOffset : ulong — added.
TrackFragmentRandomAccessBox.Entry.TrafNumber : uint — added.
TrackFragmentRandomAccessBox.Entry.TrunNumber : uint — added.
TrackFragmentRandomAccessBox.Entry.SampleNumber : uint — added.
TrackFragmentRandomAccessBox.Entries : Entry[] — added.
TrackFragmentRandomAccessBox.Read(BmffStream stream) — implemented.
TrackFragmentRandomAccessBox.Write(BmffStream stream) — implemented.
TrackFragmentRandomAccessBox.GetPayloadSize() — added.
TrackFragmentRunBox.DataOffset : int? — added.
TrackFragmentRunBox.FirstSampleFlags : uint? — added.
TrackFragmentRunBox.Entry — added nested value type.
TrackFragmentRunBox.Entry.Duration : uint — added.
TrackFragmentRunBox.Entry.Size : uint — added.
TrackFragmentRunBox.Entry.Flags : uint — added.
TrackFragmentRunBox.Entry.CompositionTimeOffset : long — added.
TrackFragmentRunBox.Entries : Entry[] — added.
TrackFragmentRunBox.Read(BmffStream stream) — implemented.
TrackFragmentRunBox.Write(BmffStream stream) — implemented.
TrackFragmentRunBox.GetPayloadSize() — added.
TrackHeaderBox.CreationTime : ulong — added.
TrackHeaderBox.ModificationTime : ulong — added.
TrackHeaderBox.TrackId : uint — added.
TrackHeaderBox.Reserved1 : uint — added.
TrackHeaderBox.Duration : ulong — added.
TrackHeaderBox.Reserved : byte[] — added.
TrackHeaderBox.Layer : short — added.
TrackHeaderBox.AlternateGroup : short — added.
TrackHeaderBox.Volume : FixedPoint8x8 — added.
TrackHeaderBox.Reserved2 : ushort — added.
TrackHeaderBox.Matrix : int[] — added.
TrackHeaderBox.Width : UnsignedFixedPoint16x16 — added.
TrackHeaderBox.Height : UnsignedFixedPoint16x16 — added.
TrackHeaderBox.Read(BmffStream stream) — implemented.
TrackHeaderBox.Write(BmffStream stream) — implemented.
TrackHeaderBox.GetPayloadSize() — added.
TrackReferenceTypeBox — added sealed box type.
TrackReferenceTypeBox.Create(FourCC type) — added.
TrackReferenceTypeBox.IsLeaf : bool — added.
TrackReferenceTypeBox.IsComposite : bool — added.
TrackReferenceTypeBox.Limit : long — added.
TrackReferenceTypeBox.Offset : long — added.
TrackReferenceTypeBox.BoxType : BmffBoxType — added.
TrackReferenceTypeBox.TrackIds : uint[] — added.
TrackReferenceTypeBox.Read(BmffStream stream) — added.
TrackReferenceTypeBox.Write(BmffStream stream) — added.
TrackReferenceTypeBox.GetPayloadSize() — added.
TrackReferenceTypeBox.Accept<T>(IBmffBoxVisitor<T> visitor) — added.
TrackReferenceTypeBox.Accept(IBmffBoxVisitor visitor) — added.
VideoMediaHeaderBox.GraphicsMode : ushort — added.
VideoMediaHeaderBox.OpColorRed : ushort — added.
VideoMediaHeaderBox.OpColorGreen : ushort — added.
VideoMediaHeaderBox.OpColorBlue : ushort — added.
VideoMediaHeaderBox.Read(BmffStream stream) — implemented.
VideoMediaHeaderBox.Write(BmffStream stream) — implemented.
VideoMediaHeaderBox.GetPayloadSize() — added.
XmlBox.Xml : string — added.
XmlBox.Read(BmffStream stream) — implemented.
XmlBox.Write(BmffStream stream) — implemented.
XmlBox.GetPayloadSize() — added.
```

</details>

## Inspection public surface inventory

O-C5 changes relative to 6cf8f88ea, one declaration per line. Removed overrides leave the inherited
member available; constructor signatures, Accept and both visitor interfaces are unchanged.
The protected description hook is included because profile subclasses implement it.

<details>
<summary>Complete per-member inspection inventory</summary>

```text
BmffBox.Offset : long — abstract getter replaced by virtual header-derived getter; source position retained after edits.
BmffBox.Limit : long — abstract getter replaced by virtual header-derived getter; original total byte count.
BmffBox.IsLeaf : bool — abstract getter replaced by virtual type-derived getter.
BmffBox.IsComposite : bool — abstract getter replaced by virtual type-derived getter.
BmffBox.WriteDescription(TextWriter) : void — added protected internal virtual hook; default writes nothing.
BmffBox — added inherited DebuggerDisplay attribute.
BmffBoxComposite — added DebuggerTypeProxy attribute; proxy type is internal.
BmffBoxWalker — added public static traversal utility.
BmffBoxWalker.Walk(BmffBox, Func<BmffBox,int,IReadOnlyList<FourCC>,BmffWalkAction>, Func<BmffBox,int,IReadOnlyList<FourCC>,BmffWalkAction>? = null, int maxDepth = 64) : bool — added.
BmffBoxWalker.Walk(BmffDocument, Func<BmffBox,int,IReadOnlyList<FourCC>,BmffWalkAction>, Func<BmffBox,int,IReadOnlyList<FourCC>,BmffWalkAction>? = null, int maxDepth = 64) : bool — added.
BmffWalkAction — added public enum.
BmffWalkAction.Continue — added.
BmffWalkAction.SkipSubtree — added.
BmffWalkAction.Stop — added.
BmffQueryExtensions — added public static extension container.
BmffBoxComposite.Children<T>() : IEnumerable<T> — added extension; T : BmffBox.
BmffBoxComposite.Descendants<T>(int maxDepth = 64) : IEnumerable<T> — added extension; T : BmffBox.
BmffBoxComposite.First<T>(int maxDepth = 64) : T — added extension; T : BmffBox.
BmffBoxComposite.FirstOrDefault<T>(int maxDepth = 64) : T? — added extension; T : BmffBox.
BmffBoxComposite.FindAll(BmffBoxType type, int maxDepth = 64) : IEnumerable<BmffBox> — added extension.
BmffBoxComposite.First(BmffBoxType type, int maxDepth = 64) : BmffBox — added extension.
BmffBoxComposite.FirstOrDefault(BmffBoxType type, int maxDepth = 64) : BmffBox? — added extension.
BmffBoxComposite.FindAll(FourCC type, int maxDepth = 64) : IEnumerable<BmffBox> — added extension.
BmffBoxComposite.First(FourCC type, int maxDepth = 64) : BmffBox — added extension.
BmffBoxComposite.FirstOrDefault(FourCC type, int maxDepth = 64) : BmffBox? — added extension.
BmffDocument.Children<T>() : IEnumerable<T> — added extension; T : BmffBox.
BmffDocument.Descendants<T>(int maxDepth = 64) : IEnumerable<T> — added extension; T : BmffBox.
BmffDocument.First<T>(int maxDepth = 64) : T — added extension; T : BmffBox.
BmffDocument.FirstOrDefault<T>(int maxDepth = 64) : T? — added extension; T : BmffBox.
BmffDocument.FindAll(BmffBoxType type, int maxDepth = 64) : IEnumerable<BmffBox> — added extension.
BmffDocument.First(BmffBoxType type, int maxDepth = 64) : BmffBox — added extension.
BmffDocument.FirstOrDefault(BmffBoxType type, int maxDepth = 64) : BmffBox? — added extension.
BmffDocument.FindAll(FourCC type, int maxDepth = 64) : IEnumerable<BmffBox> — added extension.
BmffDocument.First(FourCC type, int maxDepth = 64) : BmffBox — added extension.
BmffDocument.FirstOrDefault(FourCC type, int maxDepth = 64) : BmffBox? — added extension.
BmffTreeFormatter — added public static formatting utility.
BmffTreeFormatter.Write(BmffBox, TextWriter, int maxDepth = 64) : void — added.
BmffTreeFormatter.Format(BmffBox, int maxDepth = 64) : string — added.
BmffTreeFormatter.Write(BmffDocument, TextWriter, int maxDepth = 64) : void — added.
BmffTreeFormatter.Format(BmffDocument, int maxDepth = 64) : string — added.
BmffBoxComposite.IsLeaf : bool — removed local override; inherited BmffBox member remains.
BmffBoxComposite.IsComposite : bool — removed local override; inherited BmffBox member remains.
BmffBoxLazyLeaf.IsLeaf : bool — removed local override; inherited BmffBox member remains.
BmffBoxLazyLeaf.IsComposite : bool — removed local override; inherited BmffBox member remains.
BmffFullBox.WriteDescription(TextWriter) : void — added protected internal override.
AdditionalMetaBox.Limit : long — removed local override; inherited BmffBox member remains.
AdditionalMetaBox.Offset : long — removed local override; inherited BmffBox member remains.
BinaryXmlBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
BinaryXmlBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
BinaryXmlBox.Limit : long — removed local override; inherited BmffBox member remains.
BinaryXmlBox.Offset : long — removed local override; inherited BmffBox member remains.
ChunkOffsetBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
ChunkOffsetBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
ChunkOffsetBox.Limit : long — removed local override; inherited BmffBox member remains.
ChunkOffsetBox.Offset : long — removed local override; inherited BmffBox member remains.
ChunkOffsetBox.WriteDescription(TextWriter) : void — added protected internal override.
ChunkOffset64BitBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
ChunkOffset64BitBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
ChunkOffset64BitBox.Limit : long — removed local override; inherited BmffBox member remains.
ChunkOffset64BitBox.Offset : long — removed local override; inherited BmffBox member remains.
ChunkOffset64BitBox.WriteDescription(TextWriter) : void — added protected internal override.
CopyrightBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
CopyrightBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
CopyrightBox.Limit : long — removed local override; inherited BmffBox member remains.
CopyrightBox.Offset : long — removed local override; inherited BmffBox member remains.
DataEntryUrlBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
DataEntryUrlBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
DataEntryUrlBox.Limit : long — removed local override; inherited BmffBox member remains.
DataEntryUrlBox.Offset : long — removed local override; inherited BmffBox member remains.
DataEntryUrnBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
DataEntryUrnBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
DataEntryUrnBox.Limit : long — removed local override; inherited BmffBox member remains.
DataEntryUrnBox.Offset : long — removed local override; inherited BmffBox member remains.
DataInfoBox.Limit : long — removed local override; inherited BmffBox member remains.
DataInfoBox.Offset : long — removed local override; inherited BmffBox member remains.
DataReferenceBox.Limit : long — removed local override; inherited BmffBox member remains.
DataReferenceBox.Offset : long — removed local override; inherited BmffBox member remains.
DataReferenceBox.WriteDescription(TextWriter) : void — added protected internal override.
DisposableSampleBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
DisposableSampleBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
DisposableSampleBox.Limit : long — removed local override; inherited BmffBox member remains.
DisposableSampleBox.Offset : long — removed local override; inherited BmffBox member remains.
DisposableSampleBox.WriteDescription(TextWriter) : void — added protected internal override.
EditBox.Limit : long — removed local override; inherited BmffBox member remains.
EditBox.Offset : long — removed local override; inherited BmffBox member remains.
EditListBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
EditListBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
EditListBox.Limit : long — removed local override; inherited BmffBox member remains.
EditListBox.Offset : long — removed local override; inherited BmffBox member remains.
EditListBox.WriteDescription(TextWriter) : void — added protected internal override.
FecReservoirBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
FecReservoirBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
FecReservoirBox.Limit : long — removed local override; inherited BmffBox member remains.
FecReservoirBox.Offset : long — removed local override; inherited BmffBox member remains.
FecReservoirBox — removed local DebuggerDisplay attribute; inherits BmffBox display.
FileDeliveryItemInfoBox.Limit : long — removed local override; inherited BmffBox member remains.
FileDeliveryItemInfoBox.Offset : long — removed local override; inherited BmffBox member remains.
FileDeliveryItemInfoBox — removed local DebuggerDisplay attribute; inherits BmffBox display.
FileDeliverySessionGroupBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
FileDeliverySessionGroupBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
FileDeliverySessionGroupBox.Limit : long — removed local override; inherited BmffBox member remains.
FileDeliverySessionGroupBox.Offset : long — removed local override; inherited BmffBox member remains.
FileDeliverySessionGroupBox — removed local DebuggerDisplay attribute; inherits BmffBox display.
FilePartitionBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
FilePartitionBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
FilePartitionBox.Limit : long — removed local override; inherited BmffBox member remains.
FilePartitionBox.Offset : long — removed local override; inherited BmffBox member remains.
FilePartitionBox — removed local DebuggerDisplay attribute; inherits BmffBox display.
FileTypeBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
FileTypeBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
FileTypeBox.Limit : long — removed local override; inherited BmffBox member remains.
FileTypeBox.Offset : long — removed local override; inherited BmffBox member remains.
FileTypeBox.WriteDescription(TextWriter) : void — added protected internal override.
FreeSpaceBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
FreeSpaceBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
FreeSpaceBox.Limit : long — removed local override; inherited BmffBox member remains.
FreeSpaceBox.Offset : long — removed local override; inherited BmffBox member remains.
GroupIdToNameBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
GroupIdToNameBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
GroupIdToNameBox.Limit : long — removed local override; inherited BmffBox member remains.
GroupIdToNameBox.Offset : long — removed local override; inherited BmffBox member remains.
GroupIdToNameBox — removed local DebuggerDisplay attribute; inherits BmffBox display.
HandlerBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
HandlerBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
HandlerBox.Limit : long — removed local override; inherited BmffBox member remains.
HandlerBox.Offset : long — removed local override; inherited BmffBox member remains.
HandlerBox.WriteDescription(TextWriter) : void — added protected internal override.
HintMediaHeaderBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
HintMediaHeaderBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
HintMediaHeaderBox.Limit : long — removed local override; inherited BmffBox member remains.
HintMediaHeaderBox.Offset : long — removed local override; inherited BmffBox member remains.
HintMediaHeaderBox.WriteDescription(TextWriter) : void — added protected internal override.
IpmpControlBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
IpmpControlBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
IpmpControlBox.Limit : long — removed local override; inherited BmffBox member remains.
IpmpControlBox.Offset : long — removed local override; inherited BmffBox member remains.
IpmpControlBox — removed local DebuggerDisplay attribute; inherits BmffBox display.
IpmpInfoBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
IpmpInfoBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
IpmpInfoBox.Limit : long — removed local override; inherited BmffBox member remains.
IpmpInfoBox.Offset : long — removed local override; inherited BmffBox member remains.
IpmpInfoBox — removed local DebuggerDisplay attribute; inherits BmffBox display.
ItemInfoBox.Limit : long — removed local override; inherited BmffBox member remains.
ItemInfoBox.Offset : long — removed local override; inherited BmffBox member remains.
ItemInfoBox.WriteDescription(TextWriter) : void — added protected internal override.
ItemInfoEntryBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
ItemInfoEntryBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
ItemInfoEntryBox.Limit : long — removed local override; inherited BmffBox member remains.
ItemInfoEntryBox.Offset : long — removed local override; inherited BmffBox member remains.
ItemInfoEntryBox.WriteDescription(TextWriter) : void — added protected internal override.
ItemLocationBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
ItemLocationBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
ItemLocationBox.Limit : long — removed local override; inherited BmffBox member remains.
ItemLocationBox.Offset : long — removed local override; inherited BmffBox member remains.
ItemLocationBox.WriteDescription(TextWriter) : void — added protected internal override.
ItemProtectionBox.Limit : long — removed local override; inherited BmffBox member remains.
ItemProtectionBox.Offset : long — removed local override; inherited BmffBox member remains.
ItemProtectionBox.WriteDescription(TextWriter) : void — added protected internal override.
MediaBox.Limit : long — removed local override; inherited BmffBox member remains.
MediaBox.Offset : long — removed local override; inherited BmffBox member remains.
MediaDataBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
MediaDataBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
MediaDataBox.Limit : long — removed local override; inherited BmffBox member remains.
MediaDataBox.Offset : long — removed local override; inherited BmffBox member remains.
MediaHeaderBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
MediaHeaderBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
MediaHeaderBox.Limit : long — removed local override; inherited BmffBox member remains.
MediaHeaderBox.Offset : long — removed local override; inherited BmffBox member remains.
MediaHeaderBox.WriteDescription(TextWriter) : void — added protected internal override.
MediaInfoBox.Limit : long — removed local override; inherited BmffBox member remains.
MediaInfoBox.Offset : long — removed local override; inherited BmffBox member remains.
MetaBox.Limit : long — removed local override; inherited BmffBox member remains.
MetaBox.Offset : long — removed local override; inherited BmffBox member remains.
MetaBox.WriteDescription(TextWriter) : void — added protected internal override.
MetaRelationshipBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
MetaRelationshipBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
MetaRelationshipBox.Limit : long — removed local override; inherited BmffBox member remains.
MetaRelationshipBox.Offset : long — removed local override; inherited BmffBox member remains.
MetaRelationshipBox — removed local DebuggerDisplay attribute; inherits BmffBox display.
MovieBox.Limit : long — removed local override; inherited BmffBox member remains.
MovieBox.Offset : long — removed local override; inherited BmffBox member remains.
MovieExtensionBox.Limit : long — removed local override; inherited BmffBox member remains.
MovieExtensionBox.Offset : long — removed local override; inherited BmffBox member remains.
MovieExtensionHeaderBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
MovieExtensionHeaderBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
MovieExtensionHeaderBox.Limit : long — removed local override; inherited BmffBox member remains.
MovieExtensionHeaderBox.Offset : long — removed local override; inherited BmffBox member remains.
MovieExtensionHeaderBox.WriteDescription(TextWriter) : void — added protected internal override.
MovieFragmentBox.Limit : long — removed local override; inherited BmffBox member remains.
MovieFragmentBox.Offset : long — removed local override; inherited BmffBox member remains.
MovieFragmentHeaderBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
MovieFragmentHeaderBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
MovieFragmentHeaderBox.Limit : long — removed local override; inherited BmffBox member remains.
MovieFragmentHeaderBox.Offset : long — removed local override; inherited BmffBox member remains.
MovieFragmentHeaderBox.WriteDescription(TextWriter) : void — added protected internal override.
MovieFragmentRandomAccessBox.Limit : long — removed local override; inherited BmffBox member remains.
MovieFragmentRandomAccessBox.Offset : long — removed local override; inherited BmffBox member remains.
MovieFragmentRandomAccessOffsetBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
MovieFragmentRandomAccessOffsetBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
MovieFragmentRandomAccessOffsetBox.Limit : long — removed local override; inherited BmffBox member remains.
MovieFragmentRandomAccessOffsetBox.Offset : long — removed local override; inherited BmffBox member remains.
MovieHeaderBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
MovieHeaderBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
MovieHeaderBox.Limit : long — removed local override; inherited BmffBox member remains.
MovieHeaderBox.Offset : long — removed local override; inherited BmffBox member remains.
MovieHeaderBox.WriteDescription(TextWriter) : void — added protected internal override.
NullMediaHeaderBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
NullMediaHeaderBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
NullMediaHeaderBox.Limit : long — removed local override; inherited BmffBox member remains.
NullMediaHeaderBox.Offset : long — removed local override; inherited BmffBox member remains.
OriginalFormatBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
OriginalFormatBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
OriginalFormatBox.Limit : long — removed local override; inherited BmffBox member remains.
OriginalFormatBox.Offset : long — removed local override; inherited BmffBox member remains.
OriginalFormatBox.WriteDescription(TextWriter) : void — added protected internal override.
PartitionEntryBox.Limit : long — removed local override; inherited BmffBox member remains.
PartitionEntryBox.Offset : long — removed local override; inherited BmffBox member remains.
PrimaryItemReferenceBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
PrimaryItemReferenceBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
PrimaryItemReferenceBox.Limit : long — removed local override; inherited BmffBox member remains.
PrimaryItemReferenceBox.Offset : long — removed local override; inherited BmffBox member remains.
PrimaryItemReferenceBox.WriteDescription(TextWriter) : void — added protected internal override.
ProgressiveDownloadInfoBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
ProgressiveDownloadInfoBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
ProgressiveDownloadInfoBox.Limit : long — removed local override; inherited BmffBox member remains.
ProgressiveDownloadInfoBox.Offset : long — removed local override; inherited BmffBox member remains.
ProgressiveDownloadInfoBox.WriteDescription(TextWriter) : void — added protected internal override.
ProtectionSchemeInfoBox.Limit : long — removed local override; inherited BmffBox member remains.
ProtectionSchemeInfoBox.Offset : long — removed local override; inherited BmffBox member remains.
SampleCompositionTimeBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SampleCompositionTimeBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SampleCompositionTimeBox.Limit : long — removed local override; inherited BmffBox member remains.
SampleCompositionTimeBox.Offset : long — removed local override; inherited BmffBox member remains.
SampleCompositionTimeBox.WriteDescription(TextWriter) : void — added protected internal override.
SampleDecodingTimeBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SampleDecodingTimeBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SampleDecodingTimeBox.Limit : long — removed local override; inherited BmffBox member remains.
SampleDecodingTimeBox.Offset : long — removed local override; inherited BmffBox member remains.
SampleDecodingTimeBox.WriteDescription(TextWriter) : void — added protected internal override.
SampleDegradationPriorityBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SampleDegradationPriorityBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SampleDegradationPriorityBox.Limit : long — removed local override; inherited BmffBox member remains.
SampleDegradationPriorityBox.Offset : long — removed local override; inherited BmffBox member remains.
SampleDegradationPriorityBox — removed local DebuggerDisplay attribute; inherits BmffBox display.
SampleDescriptionBox.Limit : long — removed local override; inherited BmffBox member remains.
SampleDescriptionBox.Offset : long — removed local override; inherited BmffBox member remains.
SampleDescriptionBox.WriteDescription(TextWriter) : void — added protected internal override.
SampleGroupDescriptionBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SampleGroupDescriptionBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SampleGroupDescriptionBox.Limit : long — removed local override; inherited BmffBox member remains.
SampleGroupDescriptionBox.Offset : long — removed local override; inherited BmffBox member remains.
SampleGroupDescriptionBox.WriteDescription(TextWriter) : void — added protected internal override.
SamplePaddingBitsBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SamplePaddingBitsBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SamplePaddingBitsBox.Limit : long — removed local override; inherited BmffBox member remains.
SamplePaddingBitsBox.Offset : long — removed local override; inherited BmffBox member remains.
SamplePaddingBitsBox — removed local DebuggerDisplay attribute; inherits BmffBox display.
SampleSizeBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SampleSizeBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SampleSizeBox.Limit : long — removed local override; inherited BmffBox member remains.
SampleSizeBox.Offset : long — removed local override; inherited BmffBox member remains.
SampleSizeBox.WriteDescription(TextWriter) : void — added protected internal override.
CompactSampleSizeBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
CompactSampleSizeBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
CompactSampleSizeBox.Limit : long — removed local override; inherited BmffBox member remains.
CompactSampleSizeBox.Offset : long — removed local override; inherited BmffBox member remains.
CompactSampleSizeBox.WriteDescription(TextWriter) : void — added protected internal override.
SampleSubInfoBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SampleSubInfoBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SampleSubInfoBox.Limit : long — removed local override; inherited BmffBox member remains.
SampleSubInfoBox.Offset : long — removed local override; inherited BmffBox member remains.
SampleSubInfoBox.WriteDescription(TextWriter) : void — added protected internal override.
SampleTableBox.Limit : long — removed local override; inherited BmffBox member remains.
SampleTableBox.Offset : long — removed local override; inherited BmffBox member remains.
SampleToChunkBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SampleToChunkBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SampleToChunkBox.Limit : long — removed local override; inherited BmffBox member remains.
SampleToChunkBox.Offset : long — removed local override; inherited BmffBox member remains.
SampleToChunkBox.WriteDescription(TextWriter) : void — added protected internal override.
SampleToGroupBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SampleToGroupBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SampleToGroupBox.Limit : long — removed local override; inherited BmffBox member remains.
SampleToGroupBox.Offset : long — removed local override; inherited BmffBox member remains.
SampleToGroupBox.WriteDescription(TextWriter) : void — added protected internal override.
SchemeInformationBox.Limit : long — removed local override; inherited BmffBox member remains.
SchemeInformationBox.Offset : long — removed local override; inherited BmffBox member remains.
SchemeTypeBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SchemeTypeBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SchemeTypeBox.Limit : long — removed local override; inherited BmffBox member remains.
SchemeTypeBox.Offset : long — removed local override; inherited BmffBox member remains.
SchemeTypeBox.WriteDescription(TextWriter) : void — added protected internal override.
ShadowSyncSampleTableBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
ShadowSyncSampleTableBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
ShadowSyncSampleTableBox.Limit : long — removed local override; inherited BmffBox member remains.
ShadowSyncSampleTableBox.Offset : long — removed local override; inherited BmffBox member remains.
ShadowSyncSampleTableBox — removed local DebuggerDisplay attribute; inherits BmffBox display.
SkipBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SkipBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SkipBox.Limit : long — removed local override; inherited BmffBox member remains.
SkipBox.Offset : long — removed local override; inherited BmffBox member remains.
SoundMediaHeaderBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SoundMediaHeaderBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SoundMediaHeaderBox.Limit : long — removed local override; inherited BmffBox member remains.
SoundMediaHeaderBox.Offset : long — removed local override; inherited BmffBox member remains.
SoundMediaHeaderBox.WriteDescription(TextWriter) : void — added protected internal override.
SyncSampleTableBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
SyncSampleTableBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
SyncSampleTableBox.Limit : long — removed local override; inherited BmffBox member remains.
SyncSampleTableBox.Offset : long — removed local override; inherited BmffBox member remains.
SyncSampleTableBox.WriteDescription(TextWriter) : void — added protected internal override.
TrackBox.Limit : long — removed local override; inherited BmffBox member remains.
TrackBox.Offset : long — removed local override; inherited BmffBox member remains.
TrackExtensionBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
TrackExtensionBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
TrackExtensionBox.Limit : long — removed local override; inherited BmffBox member remains.
TrackExtensionBox.Offset : long — removed local override; inherited BmffBox member remains.
TrackExtensionBox.WriteDescription(TextWriter) : void — added protected internal override.
TrackFragmentBox.Limit : long — removed local override; inherited BmffBox member remains.
TrackFragmentBox.Offset : long — removed local override; inherited BmffBox member remains.
TrackFragmentDecodeTimeBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
TrackFragmentDecodeTimeBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
TrackFragmentDecodeTimeBox.Limit : long — removed local override; inherited BmffBox member remains.
TrackFragmentDecodeTimeBox.Offset : long — removed local override; inherited BmffBox member remains.
TrackFragmentDecodeTimeBox.WriteDescription(TextWriter) : void — added protected internal override.
TrackFragmentHeaderBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
TrackFragmentHeaderBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
TrackFragmentHeaderBox.Limit : long — removed local override; inherited BmffBox member remains.
TrackFragmentHeaderBox.Offset : long — removed local override; inherited BmffBox member remains.
TrackFragmentHeaderBox.WriteDescription(TextWriter) : void — added protected internal override.
TrackFragmentRandomAccessBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
TrackFragmentRandomAccessBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
TrackFragmentRandomAccessBox.Limit : long — removed local override; inherited BmffBox member remains.
TrackFragmentRandomAccessBox.Offset : long — removed local override; inherited BmffBox member remains.
TrackFragmentRandomAccessBox.WriteDescription(TextWriter) : void — added protected internal override.
TrackFragmentRunBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
TrackFragmentRunBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
TrackFragmentRunBox.Limit : long — removed local override; inherited BmffBox member remains.
TrackFragmentRunBox.Offset : long — removed local override; inherited BmffBox member remains.
TrackFragmentRunBox.WriteDescription(TextWriter) : void — added protected internal override.
TrackHeaderBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
TrackHeaderBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
TrackHeaderBox.Limit : long — removed local override; inherited BmffBox member remains.
TrackHeaderBox.Offset : long — removed local override; inherited BmffBox member remains.
TrackHeaderBox.WriteDescription(TextWriter) : void — added protected internal override.
TrackReferenceBox.Limit : long — removed local override; inherited BmffBox member remains.
TrackReferenceBox.Offset : long — removed local override; inherited BmffBox member remains.
TrackReferenceTypeBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
TrackReferenceTypeBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
TrackReferenceTypeBox.Limit : long — removed local override; inherited BmffBox member remains.
TrackReferenceTypeBox.Offset : long — removed local override; inherited BmffBox member remains.
TrackReferenceTypeBox.WriteDescription(TextWriter) : void — added protected internal override.
TrackSelectionBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
TrackSelectionBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
TrackSelectionBox.Limit : long — removed local override; inherited BmffBox member remains.
TrackSelectionBox.Offset : long — removed local override; inherited BmffBox member remains.
TrackSelectionBox — removed local DebuggerDisplay attribute; inherits BmffBox display.
UnknownBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
UnknownBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
UnknownBox.Limit : long — removed local override; inherited BmffBox member remains.
UnknownBox.Offset : long — removed local override; inherited BmffBox member remains.
UserDataBox.Limit : long — removed local override; inherited BmffBox member remains.
UserDataBox.Offset : long — removed local override; inherited BmffBox member remains.
VideoMediaHeaderBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
VideoMediaHeaderBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
VideoMediaHeaderBox.Limit : long — removed local override; inherited BmffBox member remains.
VideoMediaHeaderBox.Offset : long — removed local override; inherited BmffBox member remains.
VideoMediaHeaderBox.WriteDescription(TextWriter) : void — added protected internal override.
XmlBox.IsLeaf : bool — removed local override; inherited BmffBox member remains.
XmlBox.IsComposite : bool — removed local override; inherited BmffBox member remains.
XmlBox.Limit : long — removed local override; inherited BmffBox member remains.
XmlBox.Offset : long — removed local override; inherited BmffBox member remains.
```

</details>
