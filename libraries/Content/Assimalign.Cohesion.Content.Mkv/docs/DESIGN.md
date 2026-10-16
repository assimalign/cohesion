# Matroska and WebM design

## Intent and dependency direction

Content.Mkv owns Matroska semantics over generic EBML framing and implements Content.Media's
read-only track/sample vocabulary. Metadata edits save to separate storage with unchanged media
copied through. The package inspects containers, not codec bitstreams. The [overview](OVERVIEW.md)
shows the reader, writer and document access modes.

The arrows show direct package dependencies; each arrow means "references".

~~~mermaid
flowchart LR
    Mkv["Content.Mkv"] --> Ebml["Content.Ebml"]
    Mkv --> Media["Content.Media"]
    Ebml --> Binary["Content.Binary"]
    Media --> Binary
~~~

| Package | Responsibility |
| --- | --- |
| Content | Identity, stream ownership and classified format errors |
| Content.Binary | Exact reads, bounded streams and lazy BinarySegments |
| Content.Ebml | VINTs, framing, typed values, schema validation, tree and writer |
| Content.Media | Track descriptors, rational timestamps, sample indexes and seek ranges |
| Content.Mkv | Matroska/WebM vocabulary, blocks, timeline mapping and indexed metadata edits |

No BMFF dependency, shipped-to-shipped internal visibility, runtime schema loading, reflection,
dynamic code or codec processing is introduced. Inherited NativeAOT compatibility stays enabled.

<!-- Deviates from the repo documentation.md same-commit rule: the Stage 4 follow-up brief
explicitly orders consolidated design/overview updates as the fourth commit. -->

## Specifications and schema composition

The governing documents are [RFC 9559](https://www.rfc-editor.org/rfc/rfc9559.html),
[RFC 8794](https://www.rfc-editor.org/rfc/rfc8794.html), the
[Matroska element table](https://github.com/ietf-wg-cellar/matroska-specification/blob/master/ebml_matroska.xml)
and [WebM container guidelines](https://www.webmproject.org/docs/container/).
The retained table was consulted on 2026-10-16. Static C# definitions record its wire facts; no
XML parser or downloaded schema runs in the shipped library or tests.

MkvSchema composes EbmlSchema.Header with EbmlSchemaBuilder. The immutable registry maps the
ordinal DocTypes matroska and webm to their schemas. WebM retains typed definitions but assigns
maxOccurs=0 to known elements outside its retained subset. Unregistered IDs still follow EBML's
opaque preservation path. The document additionally checks supported track/codec combinations
(VP8/VP9/AV1 video, Vorbis/Opus audio and the four WebVTT identifiers with their native track kinds).
It rejects unsupported header read versions and requires EBMLMaxIDLength=4. Generic Matroska
CodecID strings pass through without parsing or normalization.

EBML now treats the active schema as the authority for element IDs. ChapterDisplay 0x80 is explicitly
defined here and parses, validates and writes despite failing VInt.IsValidIdentifier's generic rule.
Undefined IDs must still pass that generic rule, including in lenient mode. A global exception list
in VInt was rejected: it would authorize Matroska IDs in unrelated schemas and couple EBML to each
profile. No VInt public contract or alternate wire ID is needed.

A future EBML-derived package follows this pattern: reference EBML, compose its own immutable
schema/registry, reuse the reader/writer/tree, and keep semantic validation and external position
maintenance in its own adapter. Add Media only for a timed-media profile. Missing generic seams
are escalated rather than resolved with shipped-assembly InternalsVisibleTo.

## Retained wire vocabulary

Definitions carry encoded IDs, typed values, direct parents, occurrence rules, numeric ranges,
defaults and fixed binary lengths. Segment and Cluster alone permit unknown size. CRC-32 and Void
come from the RFC header schema. The complete executable inventory is src/MkvSchema.cs.

| Parent / structure | Retained children and behavior |
| --- | --- |
| Segment (18538067) | SeekHead, Info, Tracks, Cluster, Cues, Chapters, Tags, Attachments; one root |
| SeekHead / Seek | SeekID (exactly four bytes), SeekPosition; at most two SeekHeads |
| Info | TimestampScale (positive, default 1,000,000), positive Duration, DateUTC, Title, MuxingApp, WritingApp, SegmentUUID (16 nonzero-in-aggregate bytes) |
| Tracks / TrackEntry | TrackNumber/TrackUID (nonzero, unique), TrackType, FlagEnabled/Default/Forced/Lacing (0 or 1 with schema defaults), CodecID, lazy CodecPrivate, CodecDelay, SeekPreRoll, Language/LanguageBCP47, Name, positive DefaultDuration, reported TrackTimestampScale |
| Video | PixelWidth/Height, four PixelCrop values, DisplayWidth/Height/Unit, Colour master with opaque preserved children |
| Audio | positive finite SamplingFrequency (default 8000), OutputSamplingFrequency, positive Channels (default 1), positive BitDepth |
| TrackEntry / ContentEncodings | Master retained with opaque children; no decompression, decryption or configuration interpretation |
| Cluster (1F43B675) | Timestamp, SimpleBlock, BlockGroup / Block, BlockDuration, ReferenceBlock; live unknown-size boundary handling belongs to EBML |
| Cues / CuePoint / CueTrackPositions | CueTime, CueTrack, CueClusterPosition, CueRelativePosition, CueDuration, CueBlockNumber, CueCodecState |
| Chapters / EditionEntry / ChapterAtom | EditionUID/flags, ChapterUID/StringUID/start/end/flags; recursive ChapterAtom; ChapterDisplay with ChapString/Language/LanguageBCP47/Country |
| Tags / Tag | Targets (target type and track/edition/chapter/attachment UIDs), recursive SimpleTag (name, languages, default, string/binary value) |
| Attachments / AttachedFile | Description, name, media type, FileUID and lazy FileData |
| Globals | Void and four-byte CRC-32, with EBML's placement/occurrence rules |

All other IDs remain opaque binary leaves and retain their original bytes. First-wave non-goals
include linked segments, segment families, TrackOperation, codec state transitions, block additions,
compression/encryption interpretation, projections, detailed colour interpretation, chapter menus/
processes, ordered-chapter presentation programs and codec decoding. Chapter/colour/encoding data
preservation is not a claim to interpret those programs. Unknown-size unregistered masters cannot
be bounded safely and are rejected by EBML.

The chapter definitions are active. ChapterDisplay is an ordinary schema-resolved master, and
its children receive the same bounded parsing and schema validation as other metadata.

## Three access modes and limits

**Forward-only reader:** EbmlReader with MkvSchema.Registry works on non-seekable input.
ReadNext reads headers; enter masters and skip binary payloads to scan cluster by cluster.
Unknown-size masters end using schema-aware sibling lookahead, with no backward seek. Skip drains
non-seekable bytes with the shared pooled-buffer implementation. There is no frame-payload buffer.
MkvBlockReader.Create opens the current SimpleBlock or Block through EbmlReader.OpenPayloadStream.
ReadFrame hands out bounded ContentSliceStreams over encoded frame bytes; even a frame above the
buffer cap remains unbuffered. ReadBinary remains available when explicit materialization is wanted.

**Forward-only writer:** EbmlWriter takes the Matroska or WebM schema. Supply precomputed sizes
or use unknown-size Segment/Cluster masters on non-seekable output; seekable output also supports
back-patching. Producers finish every master explicitly. Nonzero numeric defaults require an
explicit nonempty width, or omission of the defaulted element, under RFC 9559 section 4.4.
The generic EBML writer's empty-default optimization is not suitable for those newly emitted values.

**Document:** MkvDocument.Load requires a readable seekable source. A bounded EBML preflight
checks track, cue-position and node counts before tree allocation, then EbmlDocument builds the
ordered tree. Binary leaves remain lazy. The tree and original-range snapshots use memory
proportional to element count, not payload bytes; a virtual 40 GiB block reads less than 16 KiB of
framing during loading and sample lookup. Very many small blocks can reach MaxElementCount;
raise a limit deliberately or use the streaming reader.

| Limit | Default | Enforcement |
| --- | --- | --- |
| MaxTracks | 256 | Preflight before track descriptor allocation |
| MaxCuePoints | 100,000 | Counts CueTrackPositions before allocating the tree/index |
| MaxLaceFrames | 256 (wire maximum) | Before frame/offset arrays |
| MaxAttachmentBufferLength | 16 MiB | ReadAttachmentData only; lazy FileData has no buffer allocation |
| Ebml.MaxBufferedValueLength | 16 MiB | Before materializing typed metadata |
| Ebml.MaxDepth | 64, maximum 256 | Reader nesting and tree validation |
| Ebml.MaxElementCount | 1,000,000 | Preflight and EBML tree construction |
| Layout passes | 64 | Fixed-point position planning |

Options are copied. The media adapter requires strict EBML validation; lenient preservation
remains available through the generic EBML document. This avoids treating invalid tracks as valid
shared-model descriptors.

## Block and lacing model

MkvBlock.Parse reads the track VINT, signed big-endian Int16 timestamp and flag octet. Reserved
flags are rejected; SimpleBlock keyframe/discardable and both block types' invisible/lacing flags
are exposed. Track and lace VINTs permit all data-bit patterns, including all-ones values.

No lacing means one frame. Xiph stores byte-summed sizes; EBML stores an unsigned first size and
biased signed size differences; fixed lacing divides the remaining bytes exactly. The last variable
size is the validated remainder. One-frame lacing, negative sizes, overruns, incomplete headers and
uneven fixed laces fail before slicing. MkvBlock.Parse exposes BinarySegment subranges;
MkvBlockReader exposes forward-only frame streams.
Both paths use one internal framing/lacing parser and neither copies encoded frame data.
FrameOffsets are absolute in the supplied payload-origin coordinate system.

BlockGroup without ReferenceBlock is a random access point. ReferenceBlock values and BlockDuration
remain typed native metadata. FlagLacing=0 is enforced when blocks are accessed. A missing TrackEntry
is a classified lazy sample-read error, rather than a fabricated track.

### Forward-only frame lifetime

MkvBlockReader.Create(EbmlReader, maxFrames) parses only the current block's framing, checks the
lace count before allocating at most 256 length/offset entries, and exposes track number, relative
timestamp, flags, lacing, frame count and absolute offsets. It accepts both SimpleBlock and Block;
BlockGroup ReferenceBlock/BlockDuration handling remains in the enclosing scan. This low-level
reader does not build track metadata or perform the document adapter's track/profile validation.

ReadFrame opens the next frame. It drains any unread remainder of the previous frame, even after
early disposal, and invalidates that old view. It returns null after the final frame. A frame
cannot read past its validated lace boundary. Disposing the block reader invalidates its views
without draining; the enclosing EBML reader drains the block remainder when ReadNext or Skip runs.
Advancing the EBML reader first invalidates the block payload. Shared-cursor access is sequential.

Frame streams support asynchronous reads and cancellation. Header parsing and ReadFrame advancement
are synchronous. No per-frame codec timing or payload buffering is introduced. Stream coordinates
are relative to the block payload; FrameOffsets supplies absolute source coordinates. Truncated
framing and encoded bytes retain classified absolute errors, including nested source windows.
Unknown-size Segment/Cluster traversal uses EBML's existing lookahead and needs no rewind.

## Typed chapter editing

MkvDocument.Chapters is a live `IList<MkvChapterEdition>` over the optional Chapters master.
MkvChapterEdition exposes its optional UID, default/ordered flags and chapter list. MkvChapter
exposes UID, optional string UID, exact unsigned start/end nanoseconds, hidden/enabled flags,
recursive chapters and displays. MkvChapterDisplay exposes text and ordered explicit language,
BCP 47 language and country occurrences. Empty legacy-language lists represent the implicit eng
default; BCP 47 values take precedence when a caller chooses a display. Wire spellings are retained,
without adding language-tag normalization or a chapter presentation engine.

These are native-tree views, not a separately serialized object graph. Reading never inserts
defaults; property edits change only the corresponding native leaf. Collection edits preserve
unrelated children and their order. Adding the first edition creates Chapters; removing the last
edition removes that entire optional container. Detached chapters/displays are created through
Create factories; an edition has a public parameterless constructor. Full schema validation runs
on save, so an empty edition or missing required chapter fields is rejected before output.

Start/end values use nanoseconds independently of Segment.TimestampScale and retain the full ulong
range. Load/save reject an end before its start and require an explicit end on an ordered leaf
chapter. Recursive occurrence minimums are checked at EditionEntry; recursive children remain
optional. Ordered flags are editable metadata, not a promise to execute ordered-chapter playback,
linked segments, menus or chapter processes. Unknown chapter extensions remain preserved native data.

## Media mapping and timeline

| Native source | Shared member / behavior |
| --- | --- |
| Serialized source snapshot | IMediaFile / IBinaryFile / IContent; OpenRead opens a borrowed read-only window |
| TrackEntry | IMediaFile.Tracks, in source order |
| TrackNumber, TrackType | Id; Video=1, Audio=2, Text=17, Metadata=33, otherwise Other |
| CodecID, CodecPrivate | Native CodecId string, lazy CodecPrivateData |
| LanguageBCP47, otherwise Language | Language; omitted legacy field defaults to eng |
| FlagEnabled, FlagDefault | IsEnabled and IsDefault; FlagForced stays native |
| Pixel and crop/display metadata | PixelWidth, PixelHeight, positive DisplayAspectRatio; unknown display units yield null |
| SamplingFrequency, Channels | SampleRate, ChannelCount; BitDepth stays native |
| Info.Duration * TimestampScale | File Duration rounded to nearest nanosecond, ties to even; absent stays null |
| Unscanned final track extent | Track Duration and index Count stay null |
| Cluster.Timestamp + signed relative timestamp | PTS multiplied by TimestampScale, then CodecDelay subtracted, using signed nanoseconds |
| DefaultDuration | Additional lace PTS increments; no per-frame codec-derived timing |
| No independently recoverable DTS | DecodeTime and PresentationOffset remain null |
| SimpleBlock keyframe / absent group ReferenceBlock | IsSync |
| Source-order blocks and lace order | Zero-based sample Index, ByteOffset and ByteLength |
| Cues plus bounded scans | Floor FindSampleAsync and preceding RAP SeekAsync |

TrackTimestampScale is deprecated: it is preserved and reported as an Unsupported diagnostic,
but ignored as required by this stage's brief. Timestamp arithmetic uses Int128 intermediates and
rejects unrepresentable results as InvalidValue. Duration's floating-point conversion is the
explicit nearest-nanosecond rounding boundary.

The first lace frame uses the block timestamp. Later frame timestamps are underdetermined without
DefaultDuration; total BlockDuration does not establish individual durations. Requests requiring
those times throw resource-backed NotSupportedException instead of guessing equal durations.

## Lazy lookup, cue use and live snapshots

The index expands one block/lace at a time and retains no whole-track sample table. Ordinal lookup
and PTS-floor lookup scan framing with cancellation checks. Ties choose the lowest source ordinal.
For a track with Cues, its first seek makes an O(blocks + frames) framing pass to retain source
ordinals and PTS/sync bounds per occupied cluster. Cues seed the target and RAP candidates; those
bounds prune unrelated clusters without assuming sorted timestamps or complete cues. Repeated
seeks inspect the summaries and read framing only in candidate clusters. The retained memory is
O(occupied clusters), bounded by the document node limit; cancellation never publishes a partial
summary. There is no logarithmic-time promise, and overlapping timestamp bounds can still require
a full pass. A track without Cues uses multiple O(blocks + frames) scans with bounded working
memory on every seek. Resolving native ReferenceBlock timestamps can require quadratic work.
Enumeration/lookup use synchronous reads of small framing data inside ValueTask/IAsyncEnumerable
contracts; no background I/O is claimed.

The seek range starts at cluster framing, covers intermediate samples, and overfetches interleaved
tracks. SeekPreRoll conservatively extends the start to the first available cluster. When references
exist, every referenced PTS must resolve and the range includes all media clusters, covering forward
and transitive references. Unresolved references, CodecState transitions, TrackOperation and
ContentEncodings prevent a safe range claim. Legacy EncryptedBlock interpretation is Unsupported.
A range assumes initialization metadata is already available; it is not independently playable.

Unknown-size Segment or any unknown-size Cluster, with no Cues, marks the presentation live.
A loaded document is a fixed snapshot of available bytes. It can enumerate and seek that extent,
including returning the last available floor sample for a later query; it cannot promise that this
is the final sample, answer future arrival/completion questions, refresh itself, or infer final count.
Unknown Duration alone does not imply live.

## Editing and position maintenance

The native tree supports title, tags, track names/languages/flags, chapters and attachment edits.
Typed chapter views and direct native edits share that tree. Use
MkvDocument.Save, not its exposed generic Ebml.Save, for Matroska position maintenance. Cluster
editing/reordering/removal is refused. The loaded source stays unchanged, and existing tracks and
sample byte coordinates continue to refer to that source. Reload a saved file for relocated views.

Save validates the tree, measures metadata and iterates segment-relative SeekPosition and
CueClusterPosition values until stable. Original target identities distinguish repeated top-level
elements. Changed position integers use eight payload bytes, preventing width oscillation during
repeated saves; master size VINT widths grow as needed. The algorithm recalculates positions instead
of inserting Void padding. Moving or growing the Segment header itself does not affect positions
relative to its data start. CueRelativePosition remains valid because entire clusters stay unchanged.

Unchanged elements, including every cluster, copy from recorded source ranges byte-for-byte. Known
metadata leaves preserve original encodings until changed; new scalar values go through EbmlWriter
with explicit nonempty numeric widths. EBML GetEncodedLength queries measure retained scalars
and explicit values without scratch output or binary payload reads. EBML WriteRawElement copies
complete original ranges and accounts for open
master bounds; MkvLayout retains only snapshot identity, format-specific widths and position planning.
The private counting stream and raw-copy path have been removed. Changed masters have CRC-32 removed;
unchanged subtrees retain their exact checksum bytes. Saving to the same source instance is rejected;
callers must also avoid aliases of the same file.

Only retained position fields are repaired. Unknown positional extensions, including deprecated
cluster Position fields, remain opaque and can become stale after relocation. Remove their native
index entries when removing a target; the save rejects remaining references to removed targets.
Save is not transactional: an I/O failure can leave partial destination output.

## Ownership, errors and extension points

The source is borrowed by default. Passing leaveOpen=false transfers ownership only after a
successful load; disposal closes that source. Lazy segments, tracks, indexes and OpenRead windows
borrow its lifetime. They share its cursor and must be used sequentially. The source must stay open,
unmodified and untruncated while the document is in use. IsReadOnly describes the serialized source;
pending native metadata edits are saved elsewhere. No refresh or concurrent positional-read API is
introduced.

Malformed wire input produces ContentFormatException with a code and absolute position, including
nested source windows. Framework exceptions remain for caller arguments, unsupported queries and
disposed resources. Messages come from Properties/ErrorMessages.resx.

| Code | Causes |
| --- | --- |
| Truncated | Partial EBML/block/lace header, unavailable declared bytes |
| InvalidStructure | Undeclared block track, duplicate identities, missing required metadata, lacing disabled |
| InvalidValue | Lace sizes/flags, out-of-segment indexes, crop/rate/UUID errors, unrepresentable timestamps |
| LimitExceeded | Buffer, depth, node, track, cue, lace or layout limits |
| Unsupported | Header/profile mismatch, WebM codec outside the retained set, legacy EncryptedBlock |

## Corpus and verification

tests/TestObjects/Golden contains four files generated by MkvFixtures.Create, with provenance
comments in that fixture and MkvGoldenCorpusTests. Each checked-in file is compared with regenerated
bytes. vp9-opus.webm has video/audio; metadata.mkv adds subtitles, tags, an attachment and a
typed chapter display; live.webm has unknown masters without cues; laces.mkv covers all three modes.
Codec bytes are placeholders. No downloaded test media or external codec is used.

Every test names RFC 9559 or RFC 8794 sections. The suite covers shared TruncationCorpus prefixes,
hostile lace lengths, undeclared tracks, invalid cues, oversize declarations, depth/count/buffer
limits, streaming partial reads, nested absolute coordinates, cancellation and ownership. It checks
exact unmodified saves, whole-cluster bytes through repeated growth/shrink, CRC removal, cue/scan
agreement, reordered PTS, references, a 40 GiB lazy source and 2,000 deterministic hostile headers.

## Public surface added or changed by the Stage 4 follow-up

- MkvDocument.Chapters: live editable chapter-edition list; Load/Save now validate typed chapters.
- MkvSchema.Matroska and WebM: activate the retained chapter definitions, including ChapterDisplay 0x80.
- MkvChapterEdition: public parameterless constructor; Uid, IsDefault, IsOrdered and Chapters.
- MkvChapter: Create(uid, timeStart), Uid, StringUid, TimeStart, TimeEnd, IsHidden, IsEnabled, Chapters and Displays.
- MkvChapterDisplay: Create(text), Text, Languages, LanguagesBCP47 and Countries.
- MkvBlockReader: Create(reader, maxFrames), TrackNumber, RelativeTimestamp, IsKeyframe, IsInvisible,
  IsDiscardable, Lacing, FrameCount, FrameOffsets, ReadFrame and Dispose.
- MkvBlock.Parse keeps its existing public contract and shares framing validation with MkvBlockReader.
- No existing public interface members or shipped-assembly internal visibility were added.

The brief authorizes the typed/editable and streaming capabilities without prescribing their CLR
names. The concrete names above implement those capabilities; none adds an unrelated surface.

## Escalations and remaining boundaries

1. EBML's recursive MinOccurs also applies to the recursive parent. SimpleTag and ChapterAtom use
   optional recursion in the schema; the adapter enforces their first occurrence in Tag/EditionEntry.
2. The shared Content area README still describes MKV as a scaffold; it is outside this worktree's
   owned paths and should be refreshed by the integration session with the parallel MPEG work.

The schema-ID, payload-stream, encoded-length and raw-copy escalations are resolved by #1559.
Regression coverage includes every existing golden, typed chapter add/rename/retime and recursive
limits, and seekable/non-seekable frame equivalence for all lace modes and both block types.

No codec decode/transcode, encryption implementation, attachment extraction to filesystem paths,
in-place editing, arbitrary cluster rewriting, live refresh, external position-extension repair or
complete Matroska/WebM conformance claim is made.
