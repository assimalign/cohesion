# Assimalign.Cohesion.Web.StaticFiles — Design

## Design intent

Serve files from a mounted `IFileSystem` as a single middleware-first feature package: one
`UseStaticFiles` verb on `IWebApplicationPipelineBuilder`, one internal
`IWebApplicationMiddleware` that writes responses imperatively, and zero request-time
composition. The package is deliberately a *composer of protocol primitives* — conditional
requests, range selection, content negotiation, and content-type mapping all come from
`Assimalign.Cohesion.Http` (`HttpConditionalRequest`, `HttpRangeSelector`, `HttpIfRange`,
`HttpContentNegotiation`, `HttpContentTypes`), so RFC semantics live in one tested place and
this middleware only sequences them.

Unlike ASP.NET's per-concern packaging (StaticFiles + DefaultFiles + DirectoryBrowser as
separate middlewares that must be ordered correctly), one package covers file serving,
default documents, and precompressed-asset negotiation — the Cohesion lean-dependency-tree
rule applied to a feature family that always ships together.

## Why-this-not-that decisions

- **`IFileSystem` as the content root, not a bespoke file-provider seam.** The FileSystem
  library already models mounts (physical, in-memory, aggregate, isolated-storage) with a
  richer surface than ASP.NET's `IFileProvider`, and the mount boundary doubles as the
  security boundary: the middleware cannot express a lookup outside the mounted root.
- **Middleware-first, no result types.** The 2026-07-10 direction withdrew `IResult`; this
  package writes status, headers, and body directly on `IHttpResponse`. The `#864` edge
  (serializer registry / `OnError`) was dropped from this item accordingly — errors here are
  protocol outcomes (`304`/`404`/`412`/`416`), not application faults.
- **Reject hostile paths with `404`, don't pass them along.** Under the mount prefix, a path
  containing dot segments (or `:`/NUL) is answered `404` directly rather than forwarded to
  downstream middleware. Forwarding would hand a hostile path to whatever handler comes next;
  answering is the honest owner behavior for URL space the mount claims. Outside-the-claim
  misses (plain not-found, unmapped extensions, bare directories) *do* pass through — those
  are legitimately someone else's URLs.
- **Two-layer traversal defense.** Layer 1 is an explicit gate (`StaticFilePath.
  HasUnsafeSegments`) over the decoded path: any `.`/`..` segment — split on both `/` and
  `\`, since `FileSystemPath` treats backslash as a separator — plus `:` (Windows drive/ADS
  shapes) and NUL. Layer 2 is `FileSystemPath.Parse` itself, which throws on interior dot
  segments and illegal characters; the gate runs first so hostile requests get a
  deterministic `404` instead of exception-driven flow. Non-canonical-but-safe forms
  (`/./x`) are also rejected rather than normalized — Cohesion transports do not collapse
  dot segments, and a static server has no business inventing URL equivalences.
- **Directory detection by info type, not `FileAttributes`.** Not every mount stamps
  `FileAttributes.Directory` (InMemory leaves attributes unset); every mount returns an
  `IFileSystemDirectory`-typed info. Type tests are the portable signal.
- **Strong ETag from `Size` + `UpdatedOn` (hex ticks-dash-length).** Cheap (no content
  hashing), stable per representation, and distinct across representations — a precompressed
  sibling naturally carries different size/mtime, satisfying the RFC 9110 rule that distinct
  encodings must not share a strong validator. Timestamps are normalized to UTC and
  truncated to whole seconds *for comparisons and emission* (HTTP-date resolution), while
  the ETag uses full-resolution ticks for discrimination.
- **Preconditions evaluate against the *negotiated* representation.** Sibling selection runs
  before validator derivation, so `If-None-Match`/`If-Range` compare against the validators
  of the representation that would actually be served — and a `Range` applies to the encoded
  bytes (RFC 9110 §14.2: ranges address the selected representation).
- **Single-range only; multi-range falls back to full `200`.** `multipart/byteranges`
  assembly is complexity with almost no modern client demand; the RFC permits serving the
  full representation instead. `HttpRangeSelector`'s DoS guard (16-range cap → full) rides
  along for free.
- **`Vary: Accept-Encoding` whenever a sibling exists** — including on identity responses
  and `304`s. The URL's response varies the moment a sibling is on disk, regardless of what
  this particular client received; caches must know. No siblings → no `Vary`.
- **Identity fallback instead of `406`.** When negotiation reports even identity refused
  (`identity;q=0` with no acceptable coding), the middleware serves identity anyway: for
  cacheable static assets a spec-permitted `406` punishes misconfigured clients for no
  operational gain.
- **Slash-less directory URLs redirect (`301`) before serving a default document.** Serving
  directory content at `/docs` would break every relative link inside the document;
  canonicalizing to `/docs/` first is the correctness move. The query string is preserved by
  re-encoding the parsed pairs (semantic, not byte-for-byte, fidelity — an accepted trade
  since the raw query text is not surfaced by `IHttpRequest`).
- **Unknown extensions blocked by default.** Serving unmapped types as `octet-stream` invites
  accidental exposure (config files, dotfiles); the default passes them through so the
  application decides. `ServeUnknownContentTypes` + `FallbackContentType` opt in explicitly.
- **Open the stream only after all no-body outcomes are resolved.** `304`/`412`/`416` never
  touch the file; a file that vanishes between resolution and open yields a clean `404`
  because nothing has been committed to the response yet.

## Request flow

```
GET/HEAD?  ──no──▶ next
prefix match (segment-aligned, ordinal)? ──no──▶ next
h1 percent-decode compensation (see below)
unsafe segments (../.\:/NUL)? ──yes──▶ 404 (terminal)
FileSystemPath.Parse  ──throws──▶ 404
resolve: file | directory(+default doc | 301 append-slash) | miss ──▶ next
content type (overlay map; unknown → next unless opted in)
negotiate precompressed sibling (.br/.gz, server prefers br) → validators
preconditions (RFC 9110 §13.2.2) ──▶ 304 | 412
range (GET only; If-Range gate) ──▶ 416 | single 206 | full 200
open stream → head (Content-*, ETag, Last-Modified, Accept-Ranges, Cache-Control, Vary) → body (GET)
```

## HTTP/1.1 percent-decode parity (known transport gap)

The h2/h3 transports percent-decode the request path before middleware sees it
(`HttpPath.FromUriComponent`), but the HTTP/1.1 reader currently surfaces the raw
request-target text (`HttpRequestTarget` does no decoding). Undefended, `%2e%2e` would read
as a literal segment name on h1 and as `..` on h2 — the classic encoded-traversal split.
The middleware compensates: when `Version == Http11` and the path contains `%`, it decodes
once (`Uri.UnescapeDataString`) before prefix matching and the traversal gate, so every
transport funnels the same text through the same defense. This also makes encoded *legit*
names (`my%20file.txt`) resolve on h1.

This is a deliberate, removable wart: the right fix is decode parity in the h1 transport,
filed as a follow-up work item — **remove this compensation in the same change** or h1 paths
would double-decode.

## AOT posture

No reflection anywhere: the content-type table is a `FrozenDictionary` built at composition
time, negotiation and precondition evaluation are static pure functions over value types,
and body copies use `ArrayPool<byte>` buffers. The package inherits `IsAotCompatible=true`.

## Error model

No package-specific exception types. `FileSystemException` from mount lookups is absorbed
into not-found/pass-through semantics; `ArgumentException` from `UseStaticFiles` validation
(bad prefix, bad default-document name, unparseable `Cache-Control`, missing fallback type)
surfaces at composition time. A file that shrinks mid-range-copy aborts the response with
`EndOfStreamException` rather than silently serving wrong bytes (matching the h2
truncated-body abort posture).

## Non-goals

- **Directory browsing** — deferred follow-up (explicitly out of #777's scope).
- **Fingerprinted asset manifests** — deferred behind endpoint routing (#28).
- **On-the-fly compression** — that is `Web.Compression`'s job (#779); this package only
  selects pre-existing sibling files.
- **`multipart/byteranges`** — multi-range sets serve the full representation.
- **Windows short-name (8.3) / trailing-dot equivalence defense** — the mount confines every
  lookup, so such OS-level aliasing cannot escape the root; serve dedicated mounts rather
  than pointing a physical mount at a directory whose *siblings* are sensitive.
