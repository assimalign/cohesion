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
