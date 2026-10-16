# Content area design

## Design intent

Content is the L1 foundation for reading, streaming and manipulating content. The root owns
format-neutral identity, ownership and composition contracts; format packages own their grammar,
reader, writer and document model. A static content engine builds on Text, Markdown and YAML;
MediaHub builds on media containers; OpenApi uses YAML serialization. Hosting, storage selection,
media workflows and job orchestration remain with the consuming service or its adapters.

The family must handle a 40 GB source, a non-seekable upload and hostile bytes without requiring a
whole-file allocation. Keeping these concerns in libraries lets services share format correctness
without sharing a runtime or storage backend. This record defines the architecture and acceptance
policy; the [Content foundation program plan](../../programs/CONTENT_FOUNDATION_PLAN.md) owns
sequencing, delivery work and open owner decisions. The contracts below are the target, not a claim
that every package already implements them.

## Access modes

Every container format follows the reader / writer / document triad in plan D1. The comparison is
`System.Text.Json`'s `Utf8JsonReader`, `Utf8JsonWriter` and `JsonDocument`: callers choose a streaming
surface or a document surface according to their access needs. Content's document surface also
supports mutation and saving; the analogy does not make `JsonDocument` mutable.

| Mode | Contract | Why it is separate |
| --- | --- | --- |
| Reader | Forward-only, element-by-element access over seekable or non-seekable input; descend into or skip a container; skip payloads without buffering them. | Inspecting metadata or ingesting a live upload must not materialize media payloads. Skipping on a non-seekable source may consume bytes, but never retains the whole payload. |
| Writer | Forward-only emission. Precompute or accept declared sizes on non-seekable output, or buffer one bounded unit; back-patch only when output is seekable. Choose large-size and unknown-size encodings deliberately. | Network output cannot assume a later seek, and a size field must never force buffering an entire file. |
| Document | Build a tree from the reader over a seekable source, edit it, then save through the writer. Untouched payloads remain lazy source segments and copy through on save. Recompute sizes and dependent offsets. | Random access and mutation cost metadata memory, while unchanged media stays outside the managed heap. |

Round-trip fidelity is an explicit goal: preserve unknown structure and untouched payload bytes,
and preserve the meaning of edited structure. Moving BMFF's `moov` requires updating `stco`/`co64`;
changing a Matroska layout requires updating `SeekHead`/`Cues`. A document retains access to its
source until lazy segments have been copied. Ownership is explicit, and a borrowed source is never
disposed by a slice. Non-seekable ingestion uses the reader; a document needing later random access
requires a caller-provided seekable source.

BMFF's existing composite box tree **is its document model**: `BmffBox`, `BmffBoxComposite`, visitors
and the one-class-per-box layout remain. A second document tree would duplicate both structure and
mutation rules. The program completes that design and repairs the stream layer beneath it.

The triad applies to container formats, not to the root contracts or primitive-only packages.
Text's line/token surfaces and YAML/Markdown's existing in-memory models retain their documented
scope. YAML preserves document semantics rather than comments; Markdown writes a canonical form
of its retained subset. Those policies must be explicit in their round-trip tests.

## Shared primitives

`Content.Binary` owns the exact-read, skip and copy operations, bounded `ContentSliceStream`, lazy
`BinarySegment`, span-based `BinaryContentReader`/`BinaryContentWriter`, FourCC and fixed-point
values (plan D2). Binary formats reuse these so partial reads, absolute positions and source
ownership have one implementation. Core's `OffsetStream` cannot substitute for a bounded stream
that returns short reads and supports forward-only input; Media's unchecked partial-read helper
must be retired. The [Content.Binary design](../../../libraries/Content/Assimalign.Cohesion.Content.Binary/docs/DESIGN.md)
holds the primitive contracts and trade-offs; it lands with the parallel Stage 1 implementation.

## Family map

The diagram shows the **target** direct dependencies from plan D5; every arrow means "depends on".
Text depends on Content, and YAML and Markdown each depend on both Content and Text; that unchanged
text branch is recorded in the table rather than adding nodes to this nine-node graph.

```mermaid
flowchart LR
    Root["Content"]
    Binary["Content.Binary"]
    Media["Content.Media"]
    Bmff["Content.Bmff"]
    Mpeg["Content.Mpeg"]
    Ebml["Content.Ebml"]
    Mkv["Content.Mkv"]
    Pdf["Content.Pdf"]
    Exe["Content.Exe"]
    Binary --> Root
    Media --> Binary
    Bmff --> Media
    Mpeg --> Bmff
    Ebml --> Binary
    Mkv --> Ebml
    Mkv --> Media
    Pdf --> Binary
    Exe --> Binary
```

All package names below have the `Assimalign.Cohesion.` prefix. "Today" means the direct
`CohesionProjectReference` items at base commit `f7197e97f`, without inferred transitive edges.

| Package | Role | Depends on today | Target dependency | Classification |
| --- | --- | --- | --- | --- |
| `Content` | Identity, ownership, composition and reader/writer contracts | None | None | Strategic |
| `Content.Binary` | Shared binary primitives and lazy payload references | Content | Content | Strategic |
| `Content.Text` | Unicode encoding detection, lines and tokens | Content | Content | Strategic |
| `Content.Yaml` | YAML document model, parser and emitter | Content, Content.Text | Content, Content.Text | Strategic |
| `Content.Markdown` | Markdown model, parser, canonical writer and HTML renderer | Content, Content.Text | Content, Content.Text | Strategic |
| `Content.Media` | Shared media descriptions; track/sample/timeline model planned | Content.Binary | Content.Binary | Strategic |
| `Content.Bmff` | ISO BMFF box reader, writer and composite document tree | Content.Media | Content.Media | Strategic |
| `Content.Mpeg` | MP4 profile over BMFF | None | Content.Bmff | Strategic |
| `Content.Ebml` | Generic EBML container, VInt and element model | None | Content.Binary | Strategic |
| `Content.Mkv` | Matroska profile over EBML, with shared media descriptions | Content.Bmff | Content.Ebml, Content.Media | Strategic |
| `Content.Pdf` | PDF object, document and cross-reference structure | None | Content.Binary | Strategic |
| `Content.Exe` | Executable-file inspection candidate; invocation scope undecided | Content.Binary | Content.Binary | Experimental |

The code does not yet have every target arrow: MKV still references BMFF; EBML, MPEG and PDF
reference nothing. The program replaces MKV's dependency with EBML and Media and adds Binary to
EBML and PDF and BMFF to MPEG. EBML stays on Binary because it is a generic container; the Matroska
profile adds media semantics. Dependencies point toward shared contracts and primitives, never
back from the root into a format or into another package's internals.

## Classification

**Strategic** means retained, built to the stabilization gates and shipped. It describes the
destination, not readiness at this snapshot. **Experimental** means kept in the tree, not shipped,
with scope undecided. **Archive** means a candidate for deletion on which nothing builds. No
package is archive today. These classifications follow the program plan's survey, section 1.

| Package | Classification | Reason |
| --- | --- | --- |
| `Content` | Strategic | Implemented neutral contracts anchor the family; creation-facade alignment remains program work. |
| `Content.Binary` | Strategic | Shared exact reads and lazy segments are required by every retained binary format; the base is a placeholder. |
| `Content.Text` | Strategic | Implemented encoding, line and token primitives support text formats and the static content engine. |
| `Content.Yaml` | Strategic | Implemented YAML model/parser/emitter serves OpenApi; the existing corpus records conformance gaps. |
| `Content.Markdown` | Strategic | Implemented CommonMark subset, canonical writer and HTML renderer serve static content. |
| `Content.Media` | Strategic | The shared track, sample and timeline model is needed by MP4 and Matroska; the base is scaffolding. |
| `Content.Bmff` | Strategic | Retain and complete the composite box design; it builds but its reader/writer and many boxes are incomplete. |
| `Content.Ebml` | Strategic | Tested VInt is a useful base for the generic container; reader, writer and document work remain. |
| `Content.Mkv` | Strategic | Retained Matroska profile, currently a placeholder with the wrong dependency. |
| `Content.Mpeg` | Strategic | Retained MP4 profile over BMFF, currently an empty project. |
| `Content.Pdf` | Strategic | Retained document-structure reader and manipulation surface, currently an empty project. |
| `Content.Exe` | Experimental | Pending O-C1: does process invocation belong in Content? The current placeholder declares `InvokeAsync()`. |

O-C1 remains an owner decision. The plan recommends retaining executable **inspection** here and
moving invocation outside the family, or dropping it until a consumer exists: process launching
introduces security and lifecycle ownership that format parsing does not have. This record does
not approve invocation or promote Exe to strategic status.

## Standards

These are named specification baselines, not claims of complete conformance. A package's compliance
tests identify the edition and section or example they exercise and document its retained subset.
For a living registry or online specification, fixture provenance also records the revision or
retrieval date; the online references below were reviewed on 2026-10-16.

| Package | Governing specification and edition | Scope today |
| --- | --- | --- |
| `Content`, `Content.Binary`, `Content.Media` | No independent file-format standard; family contracts and format-specific consumers govern these layers. | Root contracts implemented; Binary and the shared media model planned. |
| `Content.Bmff` | [ISO/IEC 14496-12:2026, edition 8](https://www.iso.org/standard/85596.html), with the [MP4RA registry](https://mp4ra.org/). | Composite box model and incomplete reader/writer; compliant parsing, emission and unknown-box preservation remain in progress. |
| `Content.Mpeg` | [ISO/IEC 14496-14:2020, edition 3](https://www.iso.org/standard/79110.html). | **Planned:** MP4 brands, tracks and samples over BMFF; MPEG-2 transport streams are separate scope. |
| `Content.Ebml` | [RFC 8794 (2020)](https://www.rfc-editor.org/rfc/rfc8794). | VInt encoding/decoding implemented and tested; full elements, schema, reader/writer and document model planned. |
| `Content.Mkv` | [RFC 9559 (2024)](https://datatracker.ietf.org/doc/html/rfc9559) and the [Matroska element specification, version 4 table](https://www.matroska.org/technical/elements.html). | **Planned:** segment, track, cluster, tag and cue structure over EBML; no codec decoding. |
| `Content.Pdf` | [ISO 32000-2:2020, edition 2 (PDF 2.0)](https://www.iso.org/standard/75839.html). | **Planned:** objects, document structure, tokenizer/parser and cross-reference handling, including reading PDF 1.x files; no rendering. |
| `Content.Exe` | [Microsoft PE/COFF specification, online revision 2026-09-10](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format); [System V ABI / ELF gABI, ELF object format 4.3 draft](https://gabi.xinuos.com/). | **Planned, subject to O-C1:** PE/COFF and ELF header inspection; the current invocation interface is scaffolding. |
| `Content.Yaml` | [YAML 1.2.2](https://yaml.org/spec/1.2.2/). | Model, parser and emitter with core-schema semantics; supported subset and excluded official-suite cases documented by the package. |
| `Content.Markdown` | [CommonMark 0.31.2](https://spec.commonmark.org/0.31.2/). | Retained subset with canonical Markdown output and HTML rendering; exclusions are explicit in the package design. |
| `Content.Text` | [Unicode 17.0.0, encoding forms](https://www.unicode.org/versions/Unicode17.0.0/) and [RFC 3629 (2003), UTF-8](https://www.rfc-editor.org/rfc/rfc3629). | UTF-8/16/32 detection and decoding, line endings and literal tokenization; no normalization or legacy code-page policy. |

## Error model

Plan D3 defines one malformed-input vocabulary: `ContentFormatException` (or a format-derived
type) carries `ContentFormatErrorCode` and the absolute source position, an absolute byte offset
for binary readers. Codes include `Truncated`, `InvalidStructure`, `ValueOutOfRange`,
`LimitExceeded` and `Unsupported`. Text formats may add line and column diagnostics. This lets
consumers handle failures without depending on localized message wording or a proliferation of
exception classes. Tests assert codes and positions, never message text. Argument misuse and
underlying I/O failure remain distinct from malformed format bytes.

Readers must not leak `IndexOutOfRangeException`, `ArgumentOutOfRangeException`,
`OverflowException`, `EndOfStreamException` or `InvalidCastException` for malformed input. At the
base commit the shared code enum is not yet present and EBML's errors still need migration; this
is an acceptance contract for the program, not the current behavior of every reader.

Declared sizes are untrusted (plan D4). Validate lengths, arithmetic and container boundaries
before allocating; reader options cap buffered payloads at 16 MiB by default and also cap nesting
depth. Larger document payloads stay lazy when the source permits it. If bytes would have to be
buffered beyond the cap, reject with `LimitExceeded`. Truncation, oversized lengths and excessive
depth belong in each format's malformed-input corpus. Raising a caller-selected cap is explicit;
the wire never selects an allocation budget.

## Fixtures and corpora

Plan D6 makes fixtures license-clean and self-contained. Build bytes in code, transcribe permitted
specification examples with attribution, or use small files produced by the family's own writer.
Never download test data or require developer-local media paths. Tests must run from the checkout
without network access. Existing vendored material retains its license and provenance; the YAML
corpus already records its upstream commit and excluded cases, and is not fetched during tests.

New fixtures live in `tests/TestObjects/`. Each checked-in file has an adjacent provenance note or
test comment naming its producer (writer and generation recipe, or specification edition, section
and example), the origin and license/permission, and any intentional byte edits. A malformed
variant records the valid seed and how it was truncated or mutated, so the expectation can be
reproduced without an external corpus.

Every retained format carries three complementary corpora:

1. **Golden round trips:** deterministic examples read, written through the package's own writer
   and read again; check semantics and promised byte fidelity, including unknown structure and
   untouched payloads. Canonical text formats assert their documented normalization.
2. **Malformed input:** truncate at every header byte, declare oversized lengths and exceed depth
   limits; assert the format error code and position. For a grammar that accepts all text, such as
   Markdown, test specified fallback and resource bounds instead of inventing syntax errors.
3. **Specification-mapped compliance:** map each test to an edition and section/example, cover
   the retained subset, and list unsupported cases explicitly. A passing selected corpus is not a
   claim of full specification compliance.

## Non-goals

- Codec decoding, transcoding and playback; the family describes and moves encoded content.
- PDF rendering; the retained scope is document structure and manipulation.
- DRM or encryption implementation, including key acquisition and policy.
- Media workflows, packaging jobs and orchestration; services own their lifecycle and scheduling.
- Storage adapters; file systems and blob stores integrate at the edge through content contracts.
