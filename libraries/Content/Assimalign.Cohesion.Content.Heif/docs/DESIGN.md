# Content.Heif design

## Retained scope

Owner decision O-C3 retains HEIF/AVIF still-image items as a separate package. The baseline is
[ISO/IEC 23008-12:2025](https://www.iso.org/standard/89035.html), with the established item-box
grammar of ISO/IEC 14496-12 and [AVIF v1.2.0](https://aomediacodec.github.io/av1-avif/v1.2.0.html).
[MP4RA](https://mp4ra.org/) supplies brand, property and item codes (reviewed 2026-10-16).
Tests cite clause titles where editions renumber sections. Codec configuration and image bytes
are opaque: passing these tests establishes container behavior, never codec conformance.

HEIF sequence tracks (msf1, avis, avio) are recognized but not interpreted. A future sequence
feature should reuse the MP4 profile track interpretation, not extend this item model.
Construction method 2 and external data references are not resolved. Exif payloads remain opaque
beyond the TIFF-header offset; TIFF parsing is gap #1553. CMAF remains in Content.Mpeg.

## Dependency and access modes

Content.Heif references Content.Bmff only; the other dependencies below are transitive.

```mermaid
flowchart LR
    Heif["Content.Heif"] --> Bmff["Content.Bmff"]
    Bmff --> Media["Content.Media"]
    Media --> Binary["Content.Binary"]
    Binary --> Root["Content"]
```

Forward-only access uses BmffReader with the HEIF registry: inspect Header, consume Payload or
skip; documents require seekable sources. BmffWriter precomputes sizes and emits to non-seekable
outputs. Containers retain ordered unknown children. Incomplete headers in uncounted containers
are malformed, not trailing data. Defined leaves retain their trailing bytes. Item-reference
children obtain their 16/32-bit width from iref; callers constructing v1 references set LargeIds.
References use existing visitor fallback Visit(BmffBox), because BMFF interfaces are not widened.

## Limits and error model

HeifReaderOptions bounds items, properties, extents/references, derived image dimensions, grid
inputs, nesting and buffered metadata. Wire counts are checked before arrays. Malformed fields
use ContentFormatException with a family code and absolute position. Resource limits and
truncated parsing always fail; semantic leniency belongs to the item view, not unsafe parsing.
Unknown children and unsupported versions survive through BMFF preservation. No reflection,
dynamic generation, codecs, storage adapters or inter-assembly internal access are required.

## BMFF extension seams

The colr, hvcC, av1C and idat models derive from BmffBoxLazyLeaf. BMFF captures their payloads
as borrowed BinarySegments over the seekable source without buffering. colr parses its small
colour prefix and retains the ICC profile or suffix as a slice. No child collection is exposed.
The protected ReadPayloadBytes helper checks the reader cap before materializing bytes.
BMFF SaveAsync invokes profile Write overrides synchronously with bounded streaming and
cancellation checks; it does not promise asynchronous-only I/O for profile boxes.

BMFF ReadChildren cannot consume an external full-container prefix. For header-only traversal
of iref, read its four prefix bytes from Payload before calling ((BmffStream)Payload).ReadChildren().
Normal Current materialization handles that prefix. No BMFF interface or visitor changes are made.

HEIF shares the other BMFF extension contracts without owning their implementations.
RegisterSampleEntry supplies contextual stsd dispatch when composing a profile with timed
entries; this still-image view does not interpret those tracks. RegisterRelocation contributes
transactional private-box rules using IBmffRelocationContext. Built-in relocation covers
stco/co64, local iloc, tfhd/trun, tfra/mfro, sidx and saio. See the
[complete rules](../../Assimalign.Cohesion.Content.Bmff/docs/DESIGN.md#relocation-and-mutation)
for width promotion, supported bases and unknown-reference limits.

## Item view, validation and editing

HeifDocument.Load uses the HEIF registry and owns its BMFF document; leaveOpen controls source
ownership. Create borrows an already loaded document. Both expose a snapshot of item identities,
ordered property associations, references and lazy extents. The view must stay alive through save.
An item extent uses file coordinates for construction method 0 and idat-relative coordinates for
method 1. Zero extent length extends to the end of that data source. Method 2 and external sources
are reported Unsupported. Duplicate IDs/locations, invalid extents, missing/hidden primaries,
missing property indices, absent coded-image configurations, invalid grids/overlays and derivation
cycles produce family errors. Unknown essential properties mark the item unsupported. Strict mode
throws; lenient mode records ContentFormatException diagnostics with positions. Limits always fail.
Unknown nonessential properties remain usable and round-trip in strict profile mode too.

Grid input order is dimg order; iovl retains RGBA fill, canvas size and signed placements; iden
exposes its single source. Thumbnail and alpha helpers follow incoming thmb/auxl references.
ExifData starts four plus the big-endian TIFF offset bytes into the Exif item, even across extents.
XMP is identified by mime/application-rdf+xml metadata (the actual MIME string is application/rdf+xml).
Exif, XMP and ICC payload contents are not parsed.

Rotation/mirror edits append a new property and replace only the selected item association;
they do not mutate another image sharing the original property. Replacements and thumbnails
append borrowed segments to idat through a seekable concatenation view. No unchanged image data
is copied into managed buffers. RemoveItem, RemoveExif, RemoveXmp and RemoveThumbnail delete
descriptors, locations, associations and understood references. Save physically compacts the
deleted extents out of mdat/idat. Replacement also retires the previous payload. There is no
structural-only removal mode.

Compaction removes the union of deleted ranges minus the union of retained item ranges. A byte
shared with any retained item is kept, including partial overlaps; the remaining unshared bytes
of the deleted extent are removed. Unrelated unreferenced bytes are preserved. This is a saved-file
contract, not an overwrite of the original source or a guarantee about copies elsewhere.

Surviving slices are concatenated without buffering image data. Every retained iloc extent is
relocated; zero lengths are resolved before compaction and base-relative entries are normalized.
BMFF's payload-map relocation overload also updates supported chunk, auxiliary, fragment and
index references into moved data. References into erased bytes fail before output. Failed planning
restores payloads and location fields; successful planning checkpoints once, even if output later
fails. Subsequent saves and edits use that checkpoint while item data retains borrowed source slices.
Unresolved, external or method-2 extents prevent removal because their sharing cannot be established.
Primary items and inputs still needed by derived images cannot be removed.

Save delegates to BmffDocument.Save/SaveAsync, which relocates method-0 mdat references when meta
grows or shrinks. Resolved item segments retain original source coordinates across repeated saves.
Method-0 ranges outside mdat are readable but edited saves fail Unsupported: BMFF currently
cannot relocate those ranges. This limitation is reported rather than emitting stale locations.
Use a separate output and never alias any borrowed replacement source. Low-level tree mutation
is outside the snapshot contract; create the view before relocating a loaded tree and use the
view edits thereafter. No concurrent source reads or document operations are supported.

The implementation permits nine requested brands: mif1, msf1, heic, heix, heim, heis, avif, avis,
avio. Sequence-only brands report Unsupported; a mixed file exposes its items in lenient mode
and reports unhandled tracks. Full brand-profile, pixel/bitstream and MIAF conformance is not claimed.

## Compliance evidence

The checked-in HEIC-shaped, AVIF-shaped, grid/thumbnail and Exif/XMP files are reproduced exactly
by HeifFixtures.Create and checked by HeifCorpusTests. Their [provenance](../tests/TestObjects/Corpus/PROVENANCE.md)
records the original writer recipe and specification clauses. Codec payloads are placeholders.
Every new test names an ISO/IEC 23008-12 clause title, ISO/IEC 14496-12 clause or AVIF section.

Coverage includes both ipma widths and item-ID versions, all requested boxes, reserved and
trailing bytes, unknown children/properties, strict and lenient structural failures, hidden or
missing primary items, grid counts and cycles, extent bounds/overflow, Exif offsets, signed
16/32-bit overlays, identity images, fragmented metadata, resource caps and header truncation.
The linked Content.Binary TruncationCorpus supplies every-header-prefix checks.
Absolute errors in fragmented descriptors map back to the original extent coordinates.

Streaming tests reject seeking on network-style input/output and prove a 40 GiB idat is loaded
without reading its payload. Metadata edits before mdat preserve image bytes after reload, and
repeated saves do not apply relocation twice. No codec conformance or complete HEIF/AVIF profile
certification is implied by these structural tests.

Erasure fixtures place a distinctive synthetic marker in Exif and assert its complete absence
after saving. They compare every retained image, exercise mdat and idat, multiple media boxes,
fragmented and zero-length extents, partial/complete sharing, replacement and repeated removal,
saio references and failed-planning retry. A sparse 40 GiB idat is compacted without reading its
image bytes. Shared marker bytes intentionally remain when a retained image references them.

## Source coordinates and shape

Profile boxes inherit Offset, Limit, IsLeaf and IsComposite from BmffBox. Offset is the original
absolute read position; Limit is the original total size. Edits, relocation and saving leave both
unchanged. A new profile box has zero coordinates until loaded; the writer computes its output
layout separately. Even an empty sample-entry or item container is a composite.

## Tree inspection

BmffTreeFormatter.Write accepts a box or document and a borrowed TextWriter; Format returns a
string with LF endings. Both use the bounded walker. Each line records FourCC, absolute source
offset, original total size, large-size/size-zero markers, shape, full-box version/flags and current
key fields. UnknownBox is explicitly marked unknown and preserved, with its preservation reason.
Lazy payloads show only their segment lengths. Unloaded boxes are marked and never measured by
calling a writer, so inspection does not initialize headers or compute edited output sizes.

The protected WriteDescription(TextWriter) hook lets each box append already materialized fields.
BmffFullBox supplies version/flags; overrides call the base and append fields with a leading space.
Hooks must not read payloads or mutate the tree. Numbers use invariant culture; control characters
are escaped to keep one physical line per box. MP4 opaque full-box prefixes (esds/vpcC) are labeled
lazy rather than decoded for display. HEIF reference descriptions inspect the retained memory prefix
without triggering array decoding; ICC profiles and configuration records remain untouched.

## Debugger inspection

One inherited DebuggerDisplay on BmffBox uses the raw FourCC, source offset and original header
size, with a child count for composites or a lazy marker for opaque payloads. Large totals are
shown in GiB; other totals use invariant digit grouping. Unloaded and preserved models are marked.
The composite debugger proxy exposes the existing Children list without copying or enumerating it.
Neither display evaluates field descriptions, writer size plans or payload accessors.
