# Assimalign.Cohesion.Http.Forms — Design

## Design intent

Form parsing is **application code layered on the protocol core**, not
protocol mechanics. `Assimalign.Cohesion.Http` owns the wire model
(status, headers, method, target, raw body stream); this package
interprets a request body under `application/x-www-form-urlencoded` or
`multipart/form-data` and produces a typed `IHttpFormCollection` of
key/value pairs plus uploaded files.

The split is deliberate and load-bearing:

- **Protocol-only consumers don't pay for it.** An HTTP client, reverse
  proxy, edge cache, or the DNS-over-HTTPS transport references
  `Assimalign.Cohesion.Http` and never sees a form type. Form parsing —
  with its temp-file spill, encoding decisions, and per-section limits —
  stays out of the hot protocol path.
- **Form support is an explicit opt-in.** A server-side consumer adds
  this package (directly, or transitively via `Assimalign.Cohesion.Web.Forms`)
  and gets `context.ReadFormAsync()` / `request.Form`. Nothing in the
  core changes.

The public surface is intentionally small: two content-type parsers
behind one feature (`HttpFormFeature`), reached through a property
(`request.Form`) and a method (`context.ReadFormAsync`).

## The feature-collection seam

Parsed-form state lives in `IHttpContext.Features` as an
`IHttpFormFeature`, not in the loosely-typed `IHttpContext.Items`
dictionary. This is the same strongly-typed extensibility seam sessions
and authentication use. Three consequences follow:

1. **Any implementation is honored.** `request.Form` and
   `context.ReadFormAsync(...)` resolve whatever `IHttpFormFeature` is
   installed, so middleware can swap in a richer implementation (alternate
   buffering, caching, per-part streaming) without touching call sites.
2. **The feature is the parse cache.** `IHttpFormFeature.Form` is
   get/set. A parsed collection is stored back into `Form`; a
   pre-attached collection (assigned via the `Form` setter) short-circuits
   `ReadFormAsync` so the body stream is never read. Repeat reads return
   the same instance.
3. **`request.Form` reaches through to the context.** The property hangs
   off `IHttpRequest` because a form reads as request-side state, but
   storage is `request.HttpContext.Features`. One type-keyed seam serves
   every higher-layer feature instead of bolting state onto each wire-level
   type.

### Convenience surface

| Entry point | Behavior |
|-------------|----------|
| `context.ReadFormAsync(ct)` | Installs a default `HttpFormFeature` over `context.Request` when none is present, triggers the lazy parse, caches and returns the collection. |
| `request.Form` (get) | Returns the installed feature's parsed collection, or a shared empty collection when nothing is parsed/attached. |
| `request.Form` (set) | Pre-attaches a collection: installs an `HttpFormFeature(collection)` when no feature exists, otherwise updates the installed feature's `Form`. |
| `builder.UseForms()` (Web.Forms) | Middleware that installs the feature and eagerly parses every request so downstream handlers read `request.Form` synchronously. |

## Parser design

Both parsers are **incremental streaming readers** — the whole body is
never buffered in memory just to parse it.

- **urlencoded** streams through `Internal/HttpFormReader`, a chunked
  `key=value&...` reader ported from ASP.NET Core's `FormReader`. It
  applies `ValueCountLimit`, `KeyLengthLimit`, and `ValueLengthLimit`,
  un-swaps `+` → space (HTML 4.01 §17.13.4.1), and percent-decodes.
  Repeated keys are accumulated (`Internal/KeyValueAccumulator`) and then
  comma-joined into the single-value collection (see constraint below).
- **multipart** uses `Internal/HttpMultipartFormReader` (ported from
  `Microsoft.AspNetCore.WebUtilities.MultipartReader`) over a
  `BufferedReadStream`, yielding one section at a time with its own header
  block and body stream. Value parts are read as text; file parts flow
  through `ReadFileSectionAsync`.

### Spill-to-disk for file uploads

A multipart file part is buffered in memory until it crosses
`HttpFormOptions.MemoryBufferThreshold` (64 KB default), at which point the
buffered bytes and the remainder are flushed to a temp file created with
`FileOptions.DeleteOnClose`. The resulting `HttpFormFile` owns the temp-file
lifetime through its stream factory, which reopens the file with
`FileShare.Read | FileShare.Delete` on each `OpenReadStream()`. Small uploads
never touch the disk; large uploads never balloon peak memory.

## Charset handling

The urlencoded body is decoded with the encoding named by the Content-Type
`charset` parameter (RFC 7231 §3.1.1.5), defaulting to UTF-8. Charset
resolution is a closed allow-list of built-in encodings — UTF-8, US-ASCII,
ISO-8859-1, UTF-16 (LE/BE) — with UTF-8 as the fallback for anything
unrecognized.

This is an **AOT decision, not a limitation of convenience**:
`Encoding.GetEncoding("<arbitrary name>")` would pull in the
`System.Text.Encoding.CodePages` provider, which relies on data tables and a
registration step that are not NativeAOT-friendly, and it throws on unknown
names. A closed switch over `Encoding.UTF8` / `Encoding.ASCII` /
`Encoding.Latin1` / `Encoding.Unicode` / `Encoding.BigEndianUnicode` is
reflection-free, allocation-light, and never throws on a hostile charset.

Multipart section text is always decoded as UTF-8; per-part charset honoring
is out of scope (see non-goals).

## Error model

There is no bespoke exception type. Limit violations surface as
`System.IO.InvalidDataException` mid-parse — the same type the underlying
readers throw — so callers catch one thing regardless of which limit tripped.

| Limit (`HttpFormOptions`) | Guards against | Thrown by |
|---------------------------|----------------|-----------|
| `ValueCountLimit` | Too many form entries | `HttpFormReader` |
| `KeyLengthLimit` / `ValueLengthLimit` | Oversized urlencoded key/value | `HttpFormReader` |
| `MultipartBoundaryLengthLimit` | Unbounded boundary look-ahead | `HttpFormFeature` (pre-flight) |
| `MultipartHeadersCountLimit` / `MultipartHeadersLengthLimit` | Header floods per section | `HttpMultipartFormReader` |
| `MultipartBodyLengthLimit` | Oversized section body | `HttpMultipartFormReaderStream` |
| `MemoryBufferThreshold` | Peak memory on large uploads (spills, does not throw) | `HttpFormFeature` |

`ReadFormAsync` observes its `CancellationToken` and throws
`OperationCanceledException` on cancellation. Malformed multipart parts with
no `name` are skipped (drained) rather than failing the whole form (RFC 7578
§4.2), so one bad part doesn't poison the rest.

The options surface carries only limits that are actually enforced. The
earlier `BufferBody` / `BufferBodyLengthLimit` knobs were removed: honoring
them means replacing the request body with a rewindable buffered stream so
multiple components can re-read it, which is a protocol-core concern
(`IHttpRequest.Body` is get-only here) and cannot be delivered honestly from
this layer.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` is inherited from the `libraries/`
tree, so the AOT analyzer runs at build. The package is reflection-free by
construction:

- No serializer, no `Type.GetType`, no dynamic dispatch.
- Charset resolution is a closed switch over built-in encodings (above).
- The feature collection resolves `IHttpFormFeature` by enumerating typed
  entries (`OfType<T>()`), not by reflection.

Keep it this way: adding `Encoding.GetEncoding` on user input, a reflective
model binder, or a plugin loader would break the AOT contract.

## Interface-first posture (and where it bends)

The package is interface-first for its data contracts — consumers program
against `IHttpFormCollection`, `IHttpFormFile`, `IHttpFormFileCollection`, and
`IHttpFormFeature`; the default implementations back them.

`HttpFormFeature` is the deliberate exception: it is `public sealed`, not an
`internal` implementation of `IHttpFormFeature`. It has to be — the
integration tests and `Assimalign.Cohesion.Web.Forms.UseForms()` (a different
assembly) construct it directly, and narrowing it to `internal` would break
both. Keeping the concrete feature public is the pragmatic call; middleware
that wants different behavior implements `IHttpFormFeature` instead of
subclassing. `HttpFormOptions` is likewise a public concrete options bag, the
conventional shape for a limits record.

## Family + dependency direction

| Package | Role | Depends on |
|---------|------|------------|
| `Assimalign.Cohesion.Http` | Protocol core (wire model, features seam) | `Assimalign.Cohesion.Core` |
| `Assimalign.Cohesion.Http.Forms` | Form model + streaming parsers + convenience surface | `Assimalign.Cohesion.Http` |
| `Assimalign.Cohesion.Web.Forms` | `UseForms()` pipeline middleware | `Assimalign.Cohesion.Web`, `…Http.Forms` |

Dependency direction is one-way toward the protocol core. The core never
references this package.

## Provenance

The internal readers are ports of ASP.NET Core's `WebUtilities`
primitives — `FormReader`, `KeyValueAccumulator`, `MultipartReader`,
`BufferedReadStream` — adapted to Cohesion conventions (file-scoped
namespaces, no `Microsoft.Extensions.*`, the `IHttpFormFeature` seam). They
are compiled into the assembly (not excluded); the public feature/collection
types are Cohesion-native.

## Non-goals

- **Multi-value form fields.** `IHttpFormCollection` is one
  `HttpQueryValue` per key by design; repeated keys are comma-joined. A
  multi-value indexer is a separate, larger API change and a distinct work
  item, not something to bolt on here.
- **Per-part multipart charset.** Section text is decoded as UTF-8.
- **Whole-body buffering / rewind (`BufferBody`).** That is a request-stream
  concern for the protocol/runtime layer, not the form parser.
- **Arbitrary charset support via the code-pages provider.** The built-in
  allow-list is intentional for AOT.
- **A model binder.** Mapping form fields onto typed objects belongs to the
  Web API / source-generation layer, not to form parsing.
