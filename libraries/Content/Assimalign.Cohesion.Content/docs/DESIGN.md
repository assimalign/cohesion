# Assimalign.Cohesion.Content — Design

## Design intent

The root of the Content library family: format-neutral contracts for what a piece of content *is*
(identity and format metadata), how its bytes are *reached* (streams with explicit ownership), and how
content *composes* (containers of child content). Every format package — binary containers (BMFF,
EBML), documents (PDF, Markdown, YAML), text, executables — builds on these contracts, and services
(a static content engine, MediaHub) consume content through them without referencing format packages.

The package deliberately carries **no storage concern and no format behavior**. Content may be backed
by memory, a stream, a file, or a remote source; parsing and serialization live in format packages.
This is why the previous dependency on `Assimalign.Cohesion.FileSystem` was removed: coupling the root
of the family to one storage substrate would force every format package and every service to inherit
it. FileSystem integration belongs in an adapter at the edge, not in the anchor everything depends on.

## The contract model

| Contract | Concern |
|---|---|
| `IContent` | Identity (`Name`), format metadata (`Format`, `MediaType`), size (`Length`), mutability (`IsReadOnly`), reopenability (`CanReopen`), and read access (`OpenRead`/`OpenReadAsync`) |
| `IComposableContent` | Ordered child content (`GetItemsAsync`) with ownership-aware disposal |
| `IWritableContent` | Byte replacement through caller-owned write streams (`OpenWrite`/`OpenWriteAsync`) |
| `IContentReader<TDocument>` / `IContentWriter<TDocument>` | The parser/serializer seam format packages implement for their document types |
| `ContentFormat` / `ContentKind` | Immutable format descriptor: name, classification, media types, extensions, governing specification |
| `ContentException` / `ContentFormatException` | Area exception root and the malformed-input error every format reader reports through |

Concrete implementations are `internal` (`MemoryContent`, `StreamContent`, `CompositeContent`),
created through the public `ContentFactory` — interface-first per the repo coding rules.

## Stream ownership (the load-bearing decision)

Predictable parsing and serving requires every stream's lifetime to have exactly one owner:

- **Streams handed out are caller-owned.** `OpenRead`/`OpenWrite` return streams the caller must
  dispose. For stream-backed content the returned object is a non-disposing *view*: disposing it never
  tears down the backing source, because the content instance owns that decision.
- **Backing resources are owned or borrowed at creation.** `ContentFactory.FromStream(..., leaveOpen:)`
  decides whether disposing the content disposes the source. The same rule applies to composites and
  their children (`leaveItemsOpen`).
- **Reopenability is explicit.** `CanReopen` distinguishes replayable content (memory, seekable
  streams — every read rewinds to the start) from single-use content (non-seekable sources — a second
  read throws `ContentException` instead of silently returning a mid-stream position).
- **Reads over one seekable source are sequential, not concurrent** — views share the source's
  position. Concurrent readers need in-memory content. This is documented rather than hidden behind
  locking, because the media formats will hand out large streams where implicit synchronization would
  be a performance trap.

Writes commit atomically: `IWritableContent.OpenWrite` buffers and replaces the visible bytes when the
write stream is disposed, so a failed emit never leaves half-written content visible.

## Composition

`IComposableContent` models containers: multipart bodies, media containers with tracks, documents with
embedded resources. Enumeration is `IAsyncEnumerable<IContent>` because real containers (an MP4 on
disk) discover children by parsing, which is I/O. A *pure* composite assembled from items has no
serialized form — `OpenRead` throws `NotSupportedException` — whereas format packages that parse a
container from bytes can expose both the bytes and the children.

## Format metadata

`ContentFormat` is data, not behavior: name, `ContentKind` classification, media types, file
extensions, specification reference. Format packages expose one shared descriptor for their format;
services use descriptors to classify and route without referencing format assemblies. Format
*detection* (sniffing bytes to a descriptor) is deliberately out of scope here — it is a
stabilization-feature concern that must not weigh down the contract root.

## Error model

`ContentException` is the area root (inherits `Exception` per the AGENTS area-root rule).
`ContentFormatException` adds the malformed-input `Position` and is the type every format reader
throws instead of leaking raw parsing exceptions; format packages derive from it when they need
richer diagnostics (the EBML package's existing exceptions should migrate onto this root as part of
its repair feature).

## Family dependency direction

```
Assimalign.Cohesion.Content            (this package — neutral contracts)
 ├── Content.Binary                    (shared binary parsing surface)
 │    └── Content.Media               (shared media abstractions)
 │         └── Content.Bmff → Mpeg…  (container formats and profiles)
 ├── Content.Text                      (encoding + text reading surface)
 │    ├── Content.Markdown           (text-derived formats)
 │    └── Content.Yaml               (YAML 1.2 — document model, parser, emitter)
 └── Content.Ebml → Content.Mkv       (EBML-based formats)
```

One-way, root-anchored: format packages depend on shared intermediate layers (`Binary`, `Media`,
`Text`), never on each other's internals. Consumers (OpenApi YAML serialization, the static content
engine, MediaHub) depend on the format package they need and receive these root contracts
transitively.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` (inherited). Interfaces, sealed value-like descriptors, and
plain stream plumbing — no reflection, no dynamic code, no serializers.

## Non-goals

- Format detection/sniffing and a format registry (a later stabilization feature; the descriptor shape
  is registry-ready).
- Storage adapters (FileSystem, blob stores) — edge concerns that must not re-enter the root.
- Bounded-stream/slice primitives for nested container parsing — feature `L01.01.05.03` builds those
  on top of these contracts.
- Content hashing/ETag computation — service-level concerns computable from `OpenRead`.
