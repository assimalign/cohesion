# Assimalign.Cohesion.Content.Bmff — Design

## Intent and scope

This package implements the ISO BMFF container layer: box identity and headers, bounded payloads,
forward-only reading and writing, and an editable document whose opaque payloads remain lazy.
The baseline is [ISO/IEC 14496-12](https://www.iso.org/standard/83102.html); known FourCCs are
identified by the [MP4 Registration Authority](https://mp4ra.org/registered-types/boxes).
These references define the wire format, not a claim that every media profile is implemented.

The package references Content.Media, which references Content.Binary and the root Content contracts.
MP4 and HEIF profiles belong above BMFF. There is no dependency on codecs, playback, storage,
hosting, reflection, assembly discovery or generated runtime code. NativeAOT compatibility is retained.

## Why the composite model stays

The owner explicitly retained the existing abstract BmffBox, BmffBoxComposite, one sealed class
per box, and both visitor interfaces. This is a scoped exception to the repository's interface-first
rule. The tree represents the file's ordered structure directly, lets each box own its payload
Read/Write behavior, and lets visitors inspect or transform models without merging unrelated
field layouts into a generic box.

BmffBoxComposite.Children is an editable IList rather than the former read-only enumeration.
IsLeaf/IsComposite report the structural role. Offset and Limit describe the original header position
and total size; they do not change when a model is edited. Header preserves the parsed wire header.
GetPayloadSize and the writer's size plan determine the new serialized size.

The dependencies between access modes and the shared primitives are:

```mermaid
flowchart LR
    Document["BmffDocument"] --> Reader["BmffReader"]
    Document --> Writer["BmffWriter"]
    Document --> Boxes["BmffBox composite tree"]
    Reader --> Registry["Immutable BmffBoxRegistry"]
    Reader --> Binary["Content.Binary"]
    Writer --> Boxes
    Writer --> Binary
    Boxes --> Media["Content.Media"]
    Media --> Binary
    Binary --> Root["Content contracts"]
```

## Three access modes

| Mode | Intended use | Payload behavior |
| --- | --- | --- |
| Reader | Inspect a large file or consume network input | Header first; consume, descend or skip before advancing |
| Writer | Save a tree or emit one fragment at a time | Precompute sizes, then copy payloads without seeking |
| Document | Inspect and edit an ordered tree from a seekable source | Materialize supported metadata; retain media and opaque ranges as BinarySegments |

### Reader

BmffReader.Create borrows a readable stream at its current position. Read reads the next header,
skipping the previous unread payload if necessary. Header is available immediately; accessing Current
materializes that box. Payload exposes an exclusive bounded stream, valid until the reader advances
or is disposed. ReadChildren enters a supported container after its prefix, preserving registry,
limits and absolute positions. Finish the child reader before using its parent again.

Seekable skips reposition the source without reading payload bytes. Forward-only input cannot reach
a subsequent header without consuming the intervening bytes: callers can consume Payload directly,
or Skip/Read drain it through a fixed pooled transfer buffer. The reader never buffers an entire mdat,
free, skip or unknown payload. Accessing Current for these boxes requires seekable input; on a
non-seekable source use Header and Payload instead. A document intentionally requires seekable input.

BmffStream uses ContentSliceStream for reads. Offset is absolute; Position and Length are relative to
the window. Reads return no more than remains, short delivery is legal, nested windows remain inside
their parent, and writes cannot cross the window boundary. Its public ReadChildren method lets a
profile parse a custom prefix from a payload before enumerating that payload's remaining children.

### Writer

BmffWriter computes the complete tree's sizes before emitting its first header. It detects cycles
and excessive nesting. Each model writes only its payload, into a bounded BmffStream. A mismatched
GetPayloadSize/Write implementation fails with InvalidStructure. Profile composite overrides are
honored; their GetPayloadSize can extend base.GetPayloadSize for a custom prefix.

New or resized boxes use the ordinary header through uint.MaxValue and the large-size header above
that boundary. Unchanged loaded boxes retain their original header encoding, including a small box
that used a large-size header and a final size-zero box. This preservation exception is necessary for
byte equality. UUID extended types retain their network byte order. A size-zero box must remain last
at top level and cannot move inside a container.

For incremental producers, BeginContainer(type, payloadSize) writes a header and streams directly.
EndContainer verifies the supplied payload count. Omitting payloadSize buffers that container up to
the writer's cap, then emits its computed header and payload. WritePayload copies raw segment bytes,
including any prefix a producer needs. Nested streaming containers are supported. Both forms work
on non-seekable outputs; back-patching is unnecessary and is not implemented. A producer must call
EndContainer for every start; Dispose releases unfinished buffers without emitting them.

SaveAsync and WriteAsync use asynchronous output and BinarySegment copy-through for package boxes.
The retained per-box Read/Write signatures are synchronous: LoadAsync performs synchronous parsing
with cancellation checkpoints, and third-party composite Write overrides run synchronously even
when invoked by SaveAsync. Calls on a reader, writer, document, model or shared source are sequential.

### Document and ownership

BmffDocument.Boxes is mutable and ordered. Load captures Source and borrows it by default.
leaveOpen: false transfers source disposal to the document, including on a failed load.
A newly constructed document has no source. The root IContentReader<BmffDocument> and
IContentWriter<BmffDocument> seams are explicitly implemented by BmffReader and BmffWriter.

Readers, writers and BmffStream windows never dispose their supplied stream. Disposing a view does
not drain it. A lazy BinarySegment also borrows its source: keep that source open and byte-stable
through save. Sequential segment reads reposition it; concurrent reads are not supported.

Save copies lazy ranges through and leaves the output open. Use a separate destination; saving to
the document's own Source is rejected. Other aliases of the source are also unsuitable and remain
the caller's responsibility. The writer does not flush or close caller-owned outputs.

## Headers, identities and full boxes

BmffBoxType keeps the known enum values as raw FourCC codes. ToFourCC and ToBmffBoxType preserve
every bit, including unregistered and high-bit values. The enum's historic spellings are retained.

BmffBoxHeader records type, nullable total size, header size, absolute offset, UUID extended type,
and whether the wire size was zero. Header sizes are 8 or 16 bytes, plus 16 for uuid.
A size-zero header consumes the enclosing input to EOF and is legal only at top level; for a
forward-only input with no known length, TotalSize is null. Such a box necessarily is the last box.
Sizes smaller than the header, children outside their parent and Int64-unrepresentable ends are
rejected before allocating a payload.

BmffFullBox centralizes a byte Version and uint Flags constrained to 24 bits for full leaf boxes.
MovieHeaderBox retains its BmffVersion convenience property over that byte. MetaBox and
ItemProtectionBox retain composite inheritance and carry their own byte Version and 24-bit Flags.
Version zero and flags zero are supported for both. ipro also validates its 16-bit protection count
and sinf child types. The field-level leaf models remain the next stage's work.

## Registry and profile extension

BmffBoxRegistry.Default is immutable and built once from explicit delegates, with a factory for
every existing concrete box class. UnknownBox is selected for arbitrary unregistered FourCCs.
BmffBoxRegistry.CreateBuilder copies the defaults; new BmffBoxRegistryBuilder starts empty.
Register adds or replaces a factory; Build freezes a snapshot unaffected by later builder changes.
Registering a default deferred type opts into the replacement parser.

Factories receive the validated BmffBoxHeader and must return a matching BoxType. They do not
receive arbitrary wire sizes before header validation. There is no reflection or assembly scanning.
The reader propagates the chosen registry into nested containers.

A profile leaf implements Read, Write and GetPayloadSize and may derive from BmffFullBox.
It reads only its bounded payload and must consume all buffered bytes. Report unsupported versions
or flags with ContentFormatException(Unsupported), and malformed fields with the appropriate
format code and absolute offset. A profile composite can parse/write its own prefix and then call
the base child traversal. For header-only streaming of a profile-specific prefix, consume that
prefix from Payload and call BmffStream.ReadChildren; the built-in reader's ReadChildren knows the
prefix grammar of the package's own containers.

## Unknown and deferred preservation

Lenient mode preserves unregistered types and registered models that cannot parse their
version/flags. UnknownBox retains the raw type, original header, BinarySegment payload and
PreservationReason. The default registry marks deferred models unsupported without calling their
old allocating, empty or incomplete parsers. NotImplementedException from a profile parser also
uses the preservation path. Other format errors are not swallowed.

Strict mode rejects unregistered/deferred types when their header is encountered and rejects
unsupported versions when materialized. Header-only scanning does not inspect supported boxes'
version fields. On forward-only sources, preservation means leaving the bounded payload available
to the caller; it does not create an in-memory substitute.

Implemented structural models: moov, trak, mdia, minf, stbl, dinf, edts, udta, mvex, moof, traf, mfra,
meta, ipro, sinf, schi, tref, meco and paen. ftyp reads/writes its brands; free, skip and mdat preserve
their bytes lazily. SkipBox is now correctly a leaf; TrackReferenceBox and SchemeInformationBox
are correctly composites. fiin has its own counted field grammar, so it remains deferred.

## Limits and error codes

The reader defaults to a 16 MiB buffered-leaf limit and depth 64 (top-level depth zero).
The writer defaults to the same buffer and depth limits. Declared payload lengths are checked
before rentals or arrays; media and opaque segments do not consume the materialization budget.
Transfer buffers are fixed-size pooled buffers, independent of payload length. A document still
allocates one node per box and a supported ftyp allocates its validated compatible-brand array.

| Code | Meaning in this layer |
| --- | --- |
| Truncated | Partial header, missing full-box prefix or an early payload end |
| InvalidStructure | Size smaller than header, child beyond parent, nested/nonfinal zero size, child-count mismatch, incomplete parser/writer or cyclic tree |
| LimitExceeded | Buffered-payload cap, nesting cap or unsupported numeric size range |
| Unsupported | Strict unknown/deferred handling, unsupported version/flags, or attempting lazy materialization on non-seekable input |
| InvalidValue | Available to profile parsers for invalid field values |
| Unspecified | Legacy root exception constructors; not used for BMFF parsing |

Malformed wire input uses ContentFormatException and absolute byte positions. Argument exceptions
and InvalidOperationException represent API misuse, such as invalid options or consuming a payload
twice. Package-authored exception text is generated from Properties/ErrorMessages.resx.

## What #447 still owes

Stage 3 must implement the deferred fields, their versions and flags, field-level mutation and
validation, and media-dependent offsets. This stage recomputes box and ancestor sizes, but it does
not relocate stco/co64 or other references when media moves. Preserve media layout until those
models can update offsets; a byte-exact unchanged save is the supported fidelity guarantee.

The default registry preserves these models instead of interpreting their fields:

| FourCC | Model |
| --- | --- |
| `bxml` | `BinaryXmlBox` |
| `stco` | `ChunkOffsetBox` |
| `co64` | `ChunkOffset64BitBox` |
| `cprt` | `CopyrightBox` |
| `dref` | `DataReferenceBox` |
| `sdtp` | `DisposableSampleBox` |
| `elst` | `EditListBox` |
| `fecr` | `FecReservoirBox` |
| `fiin` | `FileDeliveryItemInfoBox` |
| `segr` | `FileDeliverySessionGroupBox` |
| `fpar` | `FilePartitionBox` |
| `gitn` | `GroupIdToNameBox` |
| `hdlr` | `HandlerBox` |
| `hmhd` | `HintMediaHeaderBox` |
| `ipmc` | `IpmpControlBox` |
| `imif` | `IpmpInfoBox` |
| `iinf` | `ItemInfoBox` |
| `iloc` | `ItemLocationBox` |
| `mdhd` | `MediaHeaderBox` |
| `mere` | `MetaRelationshipBox` |
| `mehd` | `MovieExtensionHeaderBox` |
| `mfhd` | `MovieFragmentHeaderBox` |
| `mfro` | `MovieFragmentRandomAccessOffsetBox` |
| `mvhd` | `MovieHeaderBox` |
| `nmhd` | `NullMediaHeaderBox` |
| `frma` | `OriginalFormatBox` |
| `pitm` | `PrimaryItemReferenceBox` |
| `pdin` | `ProgressiveDownloadInfoBox` |
| `ctts` | `SampleCompositionTimeBox` |
| `stts` | `SampleDecodingTimeBox` |
| `stdp` | `SampleDegradationPriorityBox` |
| `stsd` | `SampleDescriptionBox` |
| `sgpd` | `SampleGroupDescriptionBox` |
| `padb` | `SamplePaddingBitsBox` |
| `stsz` | `SampleSizeBox` |
| `stz2` | `CompactSampleSizeBox` |
| `subs` | `SampleSubInfoBox` |
| `stsc` | `SampleToChunkBox` |
| `sbgp` | `SampleToGroupBox` |
| `schm` | `SchemeTypeBox` |
| `stsh` | `ShadowSyncSampleTableBox` |
| `smhd` | `SoundMediaHeaderBox` |
| `stss` | `SyncSampleTableBox` |
| `trex` | `TrackExtensionBox` |
| `tfhd` | `TrackFragmentHeaderBox` |
| `tfra` | `TrackFragmentRandomAccessBox` |
| `trun` | `TrackFragmentRunBox` |
| `tkhd` | `TrackHeaderBox` |
| `tsel` | `TrackSelectionBox` |
| `vmhd` | `VideoMediaHeaderBox` |
| `xml ` | `XmlBox` |

TrackBox's legacy timing/rate properties are retained for compatibility but are not trak wire fields.
The timing fields belong to track/header models in #447. Codec sample entries, MP4/HEIF brand
validation, sample indexing, fast-start relocation, playback and transcoding are outside this layer.

## Verification

The portable xUnit/Shouldly corpus builds its own bytes in tests/TestObjects. It links
Content.Binary/tests/Shared helpers and maps header cases to ISO BMFF rule families.
It covers every pure container and deferred model, ordinary/large/UUID/zero headers, every truncated
header prefix, invalid lengths, parent bounds, depth bombs, version/flags fallback, immutable
registry extension, editable nested trees, synchronous and asynchronous save, ownership, and
streaming containers. A synthetic 40 GiB source throws on any media read. Forward-only fixtures
verify no payload is read while acquiring a header and bounded draining when advancing.
