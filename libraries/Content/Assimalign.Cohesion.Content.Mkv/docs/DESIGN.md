# Matroska and WebM design

Content.Mkv references Content.Ebml and Content.Media. Matroska IDs, constraints, block framing,
media mapping and index maintenance belong here; the EBML core stays format-neutral.

The arrows show the package dependencies.

~~~mermaid
flowchart LR
    Mkv["Content.Mkv"] --> Ebml["Content.Ebml"]
    Mkv --> Media["Content.Media"]
    Ebml --> Binary["Content.Binary"]
    Media --> Binary
~~~

## Schema and retained scope

MkvSchema composes EbmlSchema.Header with EbmlSchemaBuilder and registers the result for
the ordinal DocTypes matroska and webm. Definitions follow [RFC 9559 §5](https://www.rfc-editor.org/rfc/rfc9559.html#section-5)
and the [Matroska element table](https://github.com/ietf-wg-cellar/matroska-specification/blob/master/ebml_matroska.xml).
The retained vocabulary covers Segment, SeekHead, Info, Tracks, Video/Audio, Cluster/blocks,
Cues, Chapters, Tags, Attachments and RFC 8794 globals. Colour and ContentEncodings masters
retain opaque children. Other elements stay on EBML's unknown binary path. No codec bytes are decoded.

A future EBML-derived package should compose its own immutable schema and registry, reuse
EbmlReader/EbmlWriter/EbmlDocument, and implement its semantic adapter locally. It must not add
its rules to EBML or borrow internals across shipped assemblies.

## Escalations

- The base EBML VInt implementation rejects ID 0x80, permitted by RFC 9559 §4.2 and used by
  ChapterDisplay. Pending the integration prerequisite, the chapter definitions activate only
  when that ID is supported; otherwise Chapters stays opaque and byte-preserved. No alternate
  IDs or local EBML parser are introduced. Typed chapter editing is blocked on the core fix.
- The brief sanctions functional surfaces without concrete CLR names. The new public name
  in this item is MkvSchema (Matroska, WebM, Registry); no existing interface changes.

<!-- The Stage 4 brief orders consolidated documentation in item 4. This skeleton records
the boundary now; implementation and corpus decisions are added with their changes. -->

## Delivered access modes and source lifetime

EbmlReader.Create(source, registry: MkvSchema.Registry) performs forward-only, non-seekable
element scans. Enter Segment and each Cluster, consume scalars and Skip block payloads; no
block bytes are buffered. EbmlWriter accepts the same schema and precomputed or unknown
Segment/Cluster sizes. For nonzero numeric defaults, pass an explicit nonempty payload width
as RFC 9559 §4.4 requires. The document uses that rule for newly written values.

MkvDocument.Load implements IMediaFile over a strict EbmlDocument. Its tree retains one node
per element (bounded by MaxElementCount); block and attachment bytes are lazy BinarySegments.
This is metadata memory proportional to element count, not media payload length. MkvBlock.Parse
reads only track/timestamp/flags/lace framing and returns frame slices plus absolute offsets.
The document borrows the source by default. OpenRead returns the original serialized snapshot.
Edits go through the native Ebml tree and Save to different storage; reload to get new coordinates.

## Mapping, queries and save

Tracks implement the shared interfaces internally. TrackNumber is the media Id; CodecID is
opaque; CodecPrivate remains lazy; LanguageBCP47 precedes Language (default eng). Video uses
cropped/display dimensions; Audio exposes sampling frequency/channels. PTS is
(Cluster.Timestamp + signed block offset) * TimestampScale - CodecDelay, in signed nanoseconds.
DTS and track duration remain unknown. TrackTimestampScale is preserved and reported, but ignored.

The index never materializes a per-sample table. Count remains null. Source-order scans expand
one lace at a time, checking cancellation. Cues seed a RAP candidate; bounded-memory scans
count source ordinals and refine sparse cues under arbitrary PTS reordering. Lookup is O(blocks
+ frames); seeking can take multiple passes, and ReferenceBlock resolution can require more.
A source with unknown Segment/Cluster size and no Cues is live: this is a fixed snapshot with
lookup/seek only over available bytes, no refresh, future-arrival or final-count guarantee.
Laced timestamps after frame zero require DefaultDuration: total BlockDuration alone cannot
establish individual frame durations without decoding. Such a query is explicitly unsupported.

Save calculates metadata sizes without reading binary payloads and iterates SeekPosition and
CueClusterPosition values to a fixed point (at most 64 passes). Original target identities keep
repeated saves tied to the original source. Unchanged elements, including complete clusters,
copy byte-for-byte. CRC-32 is removed from edited masters; unchanged subtrees keep their CRC.
Cluster mutation and reordering are outside this metadata editor's contract. Unknown positional
extensions remain opaque, so their internal offsets are not repaired.

## Additional core gaps and local workarounds

- Recursive EBML MinOccurs applies to the recursive parent too. SimpleTag and ChapterAtom
  therefore have optional recursion in the schema; the Matroska adapter enforces their required
  first occurrence in Tag/EditionEntry. No EBML interface changed.
- EBML exposes no raw-element copy or encoded-length query. A private layout captures source
  ranges and measures only scalar leaves through EbmlWriter. It writes master headers through
  that writer and copies unchanged ranges directly, avoiding media reads during layout.
- EBML has no forward-only payload stream accessor. The non-seekable surface can inspect headers
  and skip blocks without buffering; frame BinarySegments require the seekable document.
- Added functional public surfaces: MkvDocument (the native document/media adapter),
  MkvReaderOptions (limits), MkvBlock and MkvLacing (block/lace descriptors). These names were
  not enumerated in the brief; no existing public interface or shipped assembly internals changed.