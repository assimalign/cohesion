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

## BMFF extension constraints and local workarounds

BMFF buffers external leaf models before calling Read and has no public lazy-range accessor.
The colr, hvcC, av1C and idat models therefore inherit BmffBoxComposite solely to select its
unbuffered read/write path, override IsLeaf/IsComposite, and expose an immutable empty Children
list. They remain wire leaves. Internally, MediaDataBox.Read obtains the borrowed BinarySegment
through the existing public seam; no payload is copied or lifetime tied to a disposed window.
This is the smallest local streaming adapter that keeps BMFF unchanged. A future BMFF public
lazy-leaf seam would let these classes derive directly from BmffBox.
BMFF SaveAsync invokes these external Write overrides synchronously, with bounded streaming
and cancellation checks. It does not promise asynchronous-only I/O for profile boxes.

BMFF ReadChildren cannot consume an external full-container prefix. For header-only traversal
of iref, read its four prefix bytes from Payload before calling ((BmffStream)Payload).ReadChildren().
Normal Current materialization handles that prefix. No BMFF interface or visitor changes are made.
