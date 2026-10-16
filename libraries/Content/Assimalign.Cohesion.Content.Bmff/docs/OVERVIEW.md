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

## Edit and save

```csharp
using var document = BmffDocument.Load(input);
var fileType = (FileTypeBox)document.Boxes[0];
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
