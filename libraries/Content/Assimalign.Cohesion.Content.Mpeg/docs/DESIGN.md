# Content.Mpeg design

MP4 (ISO/IEC 14496-14) is a profile over ISO/IEC 14496-12. This package owns sample-entry
grammars, MP4RA brand interpretation and timed-track semantics. BMFF owns headers, bounds,
generic boxes, the composite tree, unknown preservation, writers and offset relocation.
A future HEIF package would compose the same registry with image-item semantics, independently
of Mpeg. Owner decision O-C3 on HEIF/AVIF remains open; CMAF stays inside Mpeg.

The arrows show direct references; neither shared package references this profile.

```mermaid
flowchart LR
    Mpeg["Content.Mpeg"] --> Bmff["Content.Bmff"]
    Mpeg --> Media["Content.Media"]
    Bmff --> Media
    Media --> Binary["Content.Binary"]
    Binary --> Root["Content"]
```

## Extension seam

CreateRegistry copies BmffBoxRegistry defaults and registers explicit factories. LoadDocument
uses the BMFF reader and re-enters it at validated stsd entry headers because BMFF deliberately
preserves all stsd children. No header parser or container writer is duplicated. Unrecognized
sample entries remain UnknownBox. The default registry is unchanged.

Sample entries are sealed composites with preserved prefixes. The retained types are avc1, avc3,
hvc1, hev1, av01, vp09, mp4a, Opus, ac-3, ec-3, wvtt, stpp and tx3g. Visual dimensions and
compressor labels, audio channels/rate and data-reference indices are editable. Other prefix
bytes and child order survive saves. Decoder boxes avcC/hvcC/av1C/vpcC/esds/dOps/dac3/dec3 and
pasp/colr/btrt preserve opaque BinarySegments, including full-box prefixes where applicable.

The public BMFF forward-only reader/writer are the streaming surfaces; supply the profile
registry. For stsd streaming, consume the full-box/count prefix and process bounded entry
payloads, or use LoadDocument for a seekable, editable tree. Codec decoding is out of scope.

## Escalations

- BMFF always bypasses registry dispatch inside stsd. Local workaround: a second bounded
  reader pass over entry headers, with the remaining nesting budget. A contextual registry
  seam belongs in BMFF if the owner wants to remove this pass.
- BMFF buffers third-party leaves and does not expose stable segment capture. Opaque profile
  nodes use childless composite inheritance to reach its unbuffered path and reuse MediaDataBox.Read
  for stable lazy segment capture. IsLeaf is true, IsComposite false, and Children is immutable
  empty. This avoids holding disposed windows or pooled buffers. A public lazy-leaf seam would
  remove this implementation compromise.
- BMFF's typed visitor rule conflicts with the prohibition on changing its interfaces. New
  profile boxes use inherited Visit(BmffBox) dispatch; no visitor interface is widened.
- Sealed box classes follow the explicit brief's composite model exception to interface-first.
- BMFF does not relocate sidx/saio. FastStart refuses files carrying these boxes before moving
  moov; arbitrary unknown private offsets remain outside the relocation guarantee.

## Media interpretation

| Native source | Shared media member | Interpretation |
| --- | --- | --- |
| moov/trak in wire order | IMediaFile.Tracks | Bounded discovery with unique tkhd IDs |
| mvhd, then mehd, then resolved track extent | IMediaFile.Duration | Exact movie-clock duration; unknown declarations use resolved PTS ends |
| Finite seekable input | IMediaFile.IsLive | False even when moof is present |
| tkhd.track_ID and track_enabled | IMediaTrack.Id / IsEnabled | Identifier and flag bit 0; IsDefault stays null |
| hdlr.handler_type | IMediaTrack.Kind | vide → Video, soun → Audio, text/sbtl/subt/clcp → Text, meta/mdta → Metadata, otherwise Other |
| Initial stsc/trex description selection | CodecId | Native sample-entry FourCC, including unknown codes |
| Initial entry's decoder box | CodecPrivateData | Borrowed opaque payload; full-box prefixes included for esds/vpcC |
| mdhd.language | Language | ISO 639-2/T packed code mapped to the two-letter BCP 47 subtag when available; other three-letter codes retained; und → null |
| mdhd.timescale | Timescale | Positive media ticks per second |
| elst, tkhd.duration, resolved presentation extent | Track Duration | Exact effective presentation duration, including empty edits |
| Visual sample prefix | PixelWidth / PixelHeight | Encoded dimensions, null when zero or unknown |
| tkhd geometry, otherwise sample geometry and pasp | DisplayAspectRatio | Positive pre-rotation ratio; unknown clap crop returns null |
| Audio sample prefix | SampleRate / ChannelCount | Unsigned 16.16 frequency and channels; zero becomes null |
| stsz/stz2 plus trun entry counts | Samples.Count | Known count without reading media payloads |
| stts; tfdt and trun/default durations | MediaSample.DecodeTime | Media-clock DTS; fragment continuity follows decode order |
| ctts/trun composition offset and elst | PresentationTime / PresentationOffset | Effective PTS and exact PTS minus DTS |
| stsc + stco/co64 + sizes; fragment data bases | ByteOffset / ByteLength | Absolute local payload coordinates, checked against mdat |
| stss; trun/tfhd/trex sample flags | IsSync | All progressive samples sync if stss absent; fragment non-sync/dependency bits honored |
| Effective PTS floor, sync eligibility, decode interval | MediaSeekResult | Lowest-index ties, preceding sync, contiguous coverage including fragment headers |

The language correspondence is the [Library of Congress ISO 639 code table](https://www.loc.gov/standards/iso639-2/php/code_list.php),
recorded as a static switch for deterministic AOT-compatible mapping. No platform culture discovery is used.

Mp4File implements IMediaFile; internal track views implement IMediaTrack and its audio/video
specializations. Native metadata stays editable through Document. The media view is valid only
while its source and tree are unchanged. Save to a separate stream and load a fresh view after
editing or FastStart. Returned read streams borrow the file lifetime. Finite fragments do not
imply IsLive. BmffReader/BmffWriter remain the forward-only surfaces.

The index retains native tables and one descriptor per trun. Progressive validation checks count
agreement and local chunk ranges. Value cursors walk stts/ctts, stsz/stz2, stsc, stco/co64 and stss
without per-sample allocation. Random ordinal lookup scans native runs; variable-size lookup
scans only sizes preceding the target in its chunk/run. Time and seek queries use cancellable,
bounded-memory scans, including reordered PTS and lowest-index tie breaking. Seek covers every
sample in the decode interval and the moof headers for intervening fragments.

Fragments resolve trun fields over tfhd and trex defaults, signed/unsigned composition offsets,
tfdt or preceding decode extent, explicit bases, default-base-is-moof, first-traf implicit moof
bases and previous-traf data ends. Each local run must fit an mdat associated with its moof,
before the next moof. External resources and unknown sample-entry data references disable index
queries. Description changes, sample-group dependency programs and auxiliary/encryption metadata
disable safe seek.

Edit mapping supports empty edits (media_time = -1), one unit-rate media edit, and trailing empty
edits. DTS stays on the media timeline; PTS includes composition displacement and the exact
rational edit shift. Enumeration retains decode preroll; time lookup excludes samples outside
the selected media edit. Repeats, dwell and non-unit rates report Unsupported instead of
flattening an ambiguous program. Track duration includes empty edits.

FastStart moves moov before the first mdat and calls BmffDocument.RelocateMediaOffsets. It requires
progressive local data and rolls back the reordering if relocation fails. Offset promotion and
transactional reference restoration remain BMFF responsibilities. Progressive-to-fragmented
remuxing is out of scope.

Strict profile mode rejects contradictory brands with InvalidStructure; lenient mode collects
at most 256 resource-backed diagnostics. This is independent of BMFF's unknown-box strictness.
CMAF checks retain the single-track, empty initialization tables, mvex/trex, one traf per moof,
tfdt, moof-relative base, first-run data offset, increasing sequence and adjacent mdat subset.
They do not certify codec profiles, switching sets, encryption or full CMAF conformance.

Track/run discovery caps are 1024 and 1,000,000 respectively, in addition to the inherited BMFF
16 MiB prefix/table budget and depth/entry limits. Configuration and media payloads are lazy.
The native million-sample test compares load allocation against a 64-sample file and checks
zero allocation for repeated constant-size ordinal queries.

The checked-in corpus under tests/TestObjects/Goldens is regenerated by Mp4GoldenFixtures using
only BmffWriter/BmffDocument: progressive H.264+AAC-shaped, its fast-start variant, a single-track
CMAF-shaped video file with two segments, M4A audio, and delayed video with unaffected audio.
Free-space padding aligns video offsets between the progressive and fragmented goldens, so all
MediaSample fields compare equal. Codec bytes are original placeholders. Tests also cover the
previous-traf implicit base, tfhd/trex precedence, variable runs, positive edit media time, malformed
counts/offsets/children, every visual-prefix truncation and a sparse 40 GiB mdat. Composition-run
intersections determine undeclared presentation duration, including the tail beyond decode time.

## Standards references

[ISO/IEC 14496-14](https://www.iso.org/standard/79110.html),
[MP4RA brands](https://mp4ra.org/registered-types/brands),
[MP4RA codecs](https://mp4ra.org/registered-types/codecs) and
[handlers](https://mp4ra.org/registered-types/handlers) govern identifiers.
The container baseline uses ISO/IEC 14496-12's established box-definition numbering; CMAF
structure is the retained subset of [ISO/IEC 23000-19](https://www.iso.org/standard/79106.html).
Tests cite established ISO/IEC 14496-12 box-definition numbering.
Independent interoperability remains a gap: the repository has no licensed external corpus
and CI has no muxer. Fixtures use the BMFF writer and placeholder codec bytes only.

## Brand interpretation

| Brand | Exposed declaration and retained check |
| --- | --- |
| isom, iso2–iso9 | ISO BMFF compatibility; preserved exact brand distinguishes the editions |
| mp41, mp42 | MP4 version one/two; required movie, tracks and retained sample tables are validated |
| avc1 | AVC extension declaration, not a claim that all tracks are AVC |
| M4A (trailing space) | Audio-only declaration; video tracks contradict it |
| M4V (trailing space) | Video presentation declaration; at least one video track is required |
| dash, msdh, msix | DASH fragmented structure declaration; moof or mvex is required |
| cmfc, cmf2 | CMAF structural declarations; the single-track retained constraints apply, including initialization-only input |
| cmfs, cmfl | CMAF segment/chunk declarations; retained fragment constraints and at least one moof apply |

Major and compatible brands both contribute declarations. Unknown brand values remain visible and
unchanged. Neither a recognized brand nor successful validation certifies codec records, media
bitstreams, all ISO edition requirements, DASH manifests, or every CMAF media profile. styp remains
an opaque BMFF box in this stage; independent CMAF segment composition needs initialization context.

## Limits, failures and retained scope

| Failure | Result |
| --- | --- |
| Partial headers or sample-entry prefixes | Truncated with absolute end position |
| Count disagreement, invalid chunk/run range, missing required boxes | InvalidStructure at the responsible box |
| Invalid field or unrepresentable timestamp | InvalidValue at the native structure |
| Buffer, nesting, table, track, run or diagnostic cap | LimitExceeded |
| Non-unit/repeated/dwell edit program, unsupported relocation | Unsupported; native bytes remain available through the document surface |
| Query with unavailable capability | NotSupportedException from Media's contract guards |
| Invalid caller arguments | Framework argument guards |

BmffReaderOptions defaults to depth 64 and one million entries per table. The 16 MiB cap applies
to buffered metadata and sample-entry prefixes; lazy records do not need a size-derived buffer.
Limits remain per native structure, plus profile discovery caps, rather than a total document
memory budget. Cancellation is checked during scans and at enumeration boundaries. Error messages
are generated from Properties/ErrorMessages.resx. No reflection, runtime generation or LINQ is used
in parsing/index loops.

Retained: the listed sample entries, progressive and finite fragmented single-source media,
unit-rate selected edit mapping, bounded-memory range queries, lossless document saves, and
progressive fast start. The BMFF parser/writer retain the D1 streaming roles.

Out of scope: codec parsing/decoding, playback/transcoding, progressive-to-fragmented remuxing,
MPEG-2 TS, HLS/DASH manifests, HEIF/AVIF, DRM, complex edit projection, configuration-transition
seek, arbitrary private offset relocation, full CMAF certification and multi-resource ranges.

## Public declarations and review decisions

No existing shipped interface changes. The package's public surface is `Mp4Profile`, `Mp4File`,
`Mp4ReaderOptions`, `Mp4BrandFeatures` and one sealed box class per sample entry and configuration
box; the complete per-member inventory was recorded in the integration pull request for name review.
No public track implementation or index class was added: consumers use the existing Media contracts.

O-C3 remains the owner's decision: retain HEIF/AVIF in a separate profile package in Stage 5, if
desired. It must use the BMFF seams independently rather than take a dependency on Mpeg's track
interpretation. The local seam workarounds above can be removed only through a separately owned
BMFF API change; this package adds no reference back or shipped InternalsVisibleTo grant.
