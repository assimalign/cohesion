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

[ISO/IEC 14496-14](https://www.iso.org/standard/79110.html),
[MP4RA brands](https://mp4ra.org/registered-types/brands),
[MP4RA codecs](https://mp4ra.org/registered-types/codecs) and
[handlers](https://mp4ra.org/registered-types/handlers) govern identifiers.
Tests cite established ISO/IEC 14496-12 box-definition numbering.
Independent interoperability remains a gap: the repository has no licensed external corpus
and CI has no muxer. Fixtures use the BMFF writer and placeholder codec bytes only.

