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

## Sources and evidence

## Media interpretation

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
queries. Description changes and sample-group dependency programs disable safe seek.

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

## Standards references

[ISO/IEC 14496-14](https://www.iso.org/standard/79110.html),
[MP4RA brands](https://mp4ra.org/registered-types/brands),
[MP4RA codecs](https://mp4ra.org/registered-types/codecs) and
[handlers](https://mp4ra.org/registered-types/handlers) govern identifiers.
Tests cite established ISO/IEC 14496-12 box-definition numbering.
Independent interoperability remains a gap: the repository has no licensed external corpus
and CI has no muxer. Fixtures use the BMFF writer and placeholder codec bytes only.

