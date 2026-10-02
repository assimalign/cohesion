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

Handlers get the same protocol behavior without the middleware through two response helpers on
`IHttpResponse`: `SendFileAsync` (an `IFileSystemFile`, or a path inside a mount) and
`WriteStreamAsync` (a stream). The middleware and the helpers run one internal engine,
`RepresentationWriter`, so there is a single implementation of validators, preconditions,
ranges, and `HEAD` in the package (see "Response helpers" below).

## Why-this-not-that decisions

- **`IFileSystem` as the content root, not a bespoke file-provider seam.** The FileSystem
  library already models mounts (physical, in-memory, aggregate, isolated-storage) with a
  richer surface than ASP.NET's `IFileProvider`, and the mount boundary doubles as the
  security boundary: the middleware cannot express a lookup outside the mounted root.
  Lookups are mount-relative: the middleware strips the request path's leading `/` before
  calling the mount, because every provider merges a relative path against its own root while
  a leading `/` is absolute in the provider's namespace (the drive root for the physical
  provider).
- **The parameterless verb serves the web root, never the content root (#1045).**
  `UseStaticFiles()` mounts `IWebApplicationContext.WebRootPath` — `wwwroot` under the content
  root by default, resolved by the hosting runtime — and passes every request through when the
  application has no web root. Until 2026-09 it mounted `ContentRootPath`, which Web.Hosting
  never set, so it fell back to the process working directory; `.json` is a served content type,
  which exposed `appsettings*.json`. Its options are configured and validated once, when the
  verb is called, exactly like the explicit `UseStaticFiles(IFileSystem, ...)` overloads (they
  used to be rebuilt on every request).
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
unsafe segments (../.\:/NUL)? ──yes──▶ 404 (terminal)
FileSystemPath.Parse  ──throws──▶ 404
resolve: file | directory(+default doc | 301 append-slash) | miss ──▶ next
content type (overlay map; unknown → next unless opted in)
negotiate precompressed sibling (.br/.gz, server prefers br) → validators
preconditions (RFC 9110 §13.2.2) ──▶ 304 | 412
range (GET only; If-Range gate) ──▶ 416 | single 206 | full 200
open stream → head (Content-*, ETag, Last-Modified, Accept-Ranges, Cache-Control, Vary) → body (GET)
```

Deriving the validators (`RepresentationMetadata.ForFile`) and every step after it belong to the shared
`RepresentationWriter` engine, which the response helpers run as well; the steps before it are the
middleware's own.

The request path the flow starts from is `context.GetEffectivePath()` (#1056). Inside a
`Map("/static", branch)` branch that is the path below `/static`, so `branch.UseStaticFiles()` serves
`/static/app.js` from `wwwroot/app.js`. The add-a-slash redirect still builds its `Location` from the
full request path, so it stays correct inside a branch. Outside a branch the effective path is the
request path.

## Single-page-application fallback (`MapFallbackToFile`, #1056)

`app.MapFallbackToFile("index.html")` maps the application's fallback route (Web.Routing's
`MapFallback`: lowest precedence, `GET`/`HEAD`, never a file-name path, never a 405). Its handler
serves the named file from the web root through the same middleware, entered at an internal
`ServeAsync(context, path, next)` with the file's path instead of the request's. The fallback
response therefore gets the full static-files treatment: content type, validators, conditional GET,
ranges and precompressed siblings. That entry point exists because the request is never rewritten
(the Web area's effective-value model). A file path that escapes the web root is rejected when the
fallback is mapped, and a missing file answers 404.

The package takes a `Web.Routing` reference for this (a feature-to-feature reference, allowed by
the Web dependency rule). Register `UseStaticFiles()` ahead of `UseRouting()`, so an existing asset is
served before routing, and the fallback answers only what remains.

## Response helpers: `SendFileAsync` and `WriteStreamAsync` (#1061)

Before #1061 only the middleware could answer with a file under conditional and range rules; a
handler that sent a download had to re-derive RFC 9110 §13 and §14 itself. The package now
exposes three `extension(IHttpResponse)` members in `HttpResponseFileExtensions`:

```csharp
Task SendFileAsync(IFileSystemFile file, string? contentType = null, CancellationToken cancellationToken = default);
Task SendFileAsync(IFileSystem fileSystem, string path, string? contentType = null, CancellationToken cancellationToken = default);
Task WriteStreamAsync(Stream stream, string? contentType = null, HttpEntityTag? entityTag = null,
    DateTimeOffset? lastModified = null, CancellationToken cancellationToken = default);
```

Every caller of the engine, and what the engine composes, is shown below. An arrow means "calls";
the middleware's file resolution and the path overload share the traversal gate and mount lookup
in `StaticFilePath`, and everything that writes a response goes through `RepresentationWriter`,
which is the only code that touches the Http primitives.

```mermaid
flowchart TD
    Fallback["MapFallbackToFile"] --> Middleware["StaticFilesMiddleware"]
    Middleware --> Gate["StaticFilePath: gate and mount lookup"]
    PathOverload["SendFileAsync(fileSystem, path)"] --> Gate
    PathOverload --> FileOverload["SendFileAsync(file)"]
    Middleware --> Writer["RepresentationWriter"]
    FileOverload --> Writer
    Stream["WriteStreamAsync(stream)"] --> Writer
    Writer --> Conditional["HttpConditionalRequest"]
    Writer --> Ranges["HttpRangeSelector and HttpIfRange"]
```

### How the engine is factored

- **`RepresentationMetadata`** describes the selected representation independently of where its bytes
  come from: content type, length (or unknown), `ETag`, `Last-Modified` at HTTP-date precision, and the
  middleware-only `Content-Encoding`, `Cache-Control`, and `Vary: Accept-Encoding`.
  `RepresentationMetadata.ForFile` derives the strong `ETag` and `Last-Modified` from `Size` and
  `UpdatedOn` exactly as the middleware always did, so a handler and the middleware emit identical
  validators for the same file, and a cache can revalidate either URL with either tag.
- **`RepresentationWriter`** works in two steps. `TrySelectContent` evaluates the preconditions
  (`HttpConditionalRequest`) and the range (`HttpIfRange`, then `HttpRangeSelector`) and writes the
  `304`, `412`, or `416` itself. Only when it returns `true` does the caller open its content and call
  `WriteContentAsync`, which writes the `200`/`206` header section and, except for `HEAD`, the bytes.
  The split keeps the rule "open the content only after every no-body outcome is resolved" for files
  and streams alike. `SendFileAsync(context, file, …)` and `WriteStreamAsync(context, …)` are the two
  compositions of those steps.
- **The middleware** keeps what is specific to static files (the prefix match, default documents,
  the add-a-slash redirect, the content-type gate, and precompressed-sibling negotiation) and ends
  with one call: `RepresentationWriter.SendFileAsync(context, servedFile, representation, ...)`. The
  extraction changed none of its behavior; every middleware test written before the engine existed
  passes unmodified.

### Why-this-not-that decisions

- **Home: Web.StaticFiles, not the Web root or Http.** This package already owns `IFileSystem`-based
  serving and composes the RFC primitives. The Web root references only `Http`, and `Http` only `Core`;
  hosting the helpers in either would add a `FileSystem` dependency to a root every Web library takes,
  or would have meant a second copy of the engine.
- **Receiver: `IHttpResponse`.** The helpers write a response, like `WriteContentAsync` and
  `WriteProblemDetailsAsync`; they read the request (method, preconditions, `Range`, `If-Range`)
  through the `IHttpResponse.HttpContext` back-reference.
- **The path overload resolves inside a mount; it does not trust the path.** A handler that builds the
  path from a route value is the normal case, so the path runs the same two layers as a request path:
  the `HasUnsafeSegments` gate (any `.`/`..` segment split on `/` and `\`, any `:`, any NUL) and then
  `FileSystemPath.Parse`, before the mount's own lookup, and a leading `/` means the mount root. An
  unsafe, missing, or directory path is answered `404`, not thrown: request input must not turn into a
  `500`, and one answer for "outside" and "missing" gives an attacker no oracle. A physical-mount test
  proves the gate is load-bearing — with it removed, `../secret.txt` reaches the file next to the
  mount. The trusted-physical-path alternative (`SendFileAsync(string physicalPath)`, ASP.NET's shape)
  was rejected because the common bug is exactly an application concatenating user input into it.
- **Content type.** An explicit type wins; it must be a concrete media type and may not contain
  control characters, because it is written into the header section verbatim and `HttpMediaType`
  skips a malformed parameter rather than failing (a CR/LF would otherwise reach the wire). Without
  one, a file's type comes from its extension in `HttpContentTypes.Default`, and an unmapped extension
  is sent as `application/octet-stream`. That differs from the middleware, which passes unknown
  types through: the middleware guards a whole directory, while a handler that calls `SendFileAsync`
  chose this file. A stream defaults to `application/octet-stream`.
- **Stream validators are the caller's, never computed.** Hashing a stream would turn every request,
  `304`s included, into a full read of something that may be large, one-shot, or remote. Callers
  supply `entityTag` and `lastModified` when they have them (a blob version, a row timestamp). Without
  them the representation still exists, so only the `*` forms of `If-Match`/`If-None-Match` match, and
  `If-Range` always yields the full `200`. A weak tag satisfies `If-None-Match` (weak comparison) but
  never `If-Match` or `If-Range` (strong comparison), exactly as the primitives define.
- **Ranges need a seekable stream.** The representation is the stream's remaining bytes, from its
  current position to its end, so a seekable stream has a known length (`Length - Position`), gets
  `Content-Length` and `Accept-Ranges: bytes`, and reaches a range by seeking. A non-seekable stream is
  sent whole with no `Content-Length` (the transport delimits it) and no `Accept-Ranges`, and a `Range`
  is ignored, which RFC 9110 §14.2 permits. Serving ranges by reading and discarding up to the
  offset was rejected: one small request could make the server read an arbitrary amount from a backend.
  A caller-declared length for a non-seekable stream was left out as well: a stream that yields a
  different number of bytes than it declared breaks HTTP/1.1 framing.
- **`HEAD`** gets the `GET` header section and no content; the copy is skipped, so a stream is never
  read. An unknown length is omitted rather than guessed.
- **Header ownership.** The helpers set the status and own `Content-Type`, `Content-Length`,
  `Content-Range`, `Accept-Ranges`, `ETag`, and `Last-Modified`. Fields the application set first are
  left alone, so a handler's `Cache-Control` also rides on the `304` (RFC 9110 §15.4.5).
- **Cancellation.** The copy observes the caller's token, or the exchange's `RequestCancelled` when the
  caller passed none, so a forgotten token still stops a download the client abandoned.
- **Ownership.** A file is opened and disposed by the helper; a stream belongs to the caller and is not
  disposed.

## HTTP/1.1 percent-decode parity (transport-owned)

The traversal gate runs over the **decoded** request path, and every transport now decodes it
before the middleware sees it: HTTP/1.1's `Http1MessageReader` percent-decodes the origin-form
request-target through the same `HttpPath.FromUriComponent` HTTP/2 (`Http2Stream`) and HTTP/3
(`Http3HeaderCodec`) run over the `:path` pseudo-header (issue #895). So `%2e%2e` arrives here as
`..` on h1 exactly as on h2/h3, `%2F` stays encoded on all three (it never becomes a separator),
and a decoded octet that is not a legal path character (a space, control, `?`/`#`, or NUL) makes
the request-target malformed on every transport — so `my%20file.txt` is uniformly unreachable
rather than resolving on h1 alone.

Because the transport owns the single decode, **the middleware must not decode again** — it reads
`context.Request.Path.Value` verbatim. The former `Version == Http11` + `Uri.UnescapeDataString`
compensation (which double-decoded relative to `FromUriComponent`, e.g. also collapsing `%2F` to a
separator) was removed together with the h1 transport fix; re-introducing any middleware-side decode
would double-decode a path the transport already handled.

## AOT posture

No reflection anywhere: the content-type table is a `FrozenDictionary` built at composition
time, negotiation and precondition evaluation are static pure functions over value types,
and body copies use `ArrayPool<byte>` buffers. The response helpers are static extension members
over the same engine and add nothing AOT-relevant. The package inherits `IsAotCompatible=true`.

## Error model

No package-specific exception types. `FileSystemException` from mount lookups is absorbed
into not-found/pass-through semantics; `ArgumentException` from `UseStaticFiles` validation
(bad prefix, bad default-document name, unparseable `Cache-Control`, missing fallback type)
surfaces at composition time. A file that shrinks mid-range-copy aborts the response with
`EndOfStreamException` rather than silently serving wrong bytes (matching the h2
truncated-body abort posture).

The response helpers throw only for caller mistakes, synchronously, before anything is written:
`ArgumentNullException` for a missing argument, and `ArgumentException` for a content type that is
not a concrete media type or carries a control character, an unreadable stream, or an empty
entity-tag. Everything the request controls is a status code instead: the path overload answers
`404` for an unsafe, missing, or directory path, and preconditions and ranges produce `304`, `412`,
or `416`. Cancellation surfaces as `OperationCanceledException` from the copy.

## Non-goals

- **Directory browsing** — deferred follow-up (explicitly out of #777's scope).
- **Fingerprinted asset manifests** — deferred behind endpoint routing (#28).
- **On-the-fly compression** — that is `Web.Compression`'s job (#779); this package only
  selects pre-existing sibling files.
- **`multipart/byteranges`** — multi-range sets serve the full representation.
- **Windows short-name (8.3) / trailing-dot equivalence defense** — the mount confines every
  lookup, so such OS-level aliasing cannot escape the root; serve dedicated mounts rather
  than pointing a physical mount at a directory whose *siblings* are sensitive.
- **`Content-Disposition` from the helpers** — a handler that wants a download name sets the field
  before calling `SendFileAsync`/`WriteStreamAsync`, which leave it in place. `Http` has no RFC 6266
  formatter yet, and a hand-rolled `filename*=` encoder in this package is not where one belongs.
- **Computed validators for streams** — no hashing of stream content (see "Response helpers").
- **Ranges over non-seekable streams** — the full representation is sent instead.
