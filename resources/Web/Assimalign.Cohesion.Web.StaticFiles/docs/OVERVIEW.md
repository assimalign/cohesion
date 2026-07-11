# Assimalign.Cohesion.Web.StaticFiles — Overview

Static file serving for the Cohesion Web pipeline, built over the `libraries/FileSystem`
abstractions. One feature package covers file serving, default documents, conditional GET,
single byte-range responses, content-type mapping, and precompressed (`.br`/`.gz`) sibling
negotiation — composed from the shared `Assimalign.Cohesion.Http` protocol primitives rather
than re-deriving any RFC semantics locally.

## Scope

- **Content root = an `IFileSystem` mount.** Any implementation works: `PhysicalFileSystem`,
  `InMemoryFileSystem`, `AggregateFileSystem` composites. The middleware can never address
  anything outside the mount — path-traversal defense is a hard guarantee, not an option.
- **`GET`/`HEAD` only.** Everything else passes through to the next middleware.
- **Builder-time composition only.** The file system and every option are supplied at
  `UseStaticFiles(...)` and frozen into the middleware; there is no service container, no
  configuration binding, and no request-time service location (the Web-area rule).

## Usage

```csharp
using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Web.StaticFiles;

var contentRoot = new PhysicalFileSystem(new PhysicalFileSystemOptions { /* root */ });

app.UseStaticFiles(contentRoot, options =>
{
    options.RequestPath = new HttpPath("/static");     // mount prefix (default "/")
    options.CacheControl = "public, max-age=3600";     // emitted on every served response
    options.DefaultDocuments.Add("default.html");      // probed after index.html/index.htm
    options.ContentTypeMappings[".gltf"] = "model/gltf+json";
    options.ServeUnknownContentTypes = false;          // default: unmapped extensions pass through
    options.ServePrecompressedAssets = true;           // default: .br/.gz siblings negotiate
});
```

## What a served response carries

| Concern | Behavior |
|---|---|
| Validators | Strong `ETag` derived from `Size` + `UpdatedOn`; `Last-Modified` (HTTP-date). |
| Conditional GET | `If-None-Match` / `If-Modified-Since` → `304`; `If-Match` / `If-Unmodified-Since` → `412` (RFC 9110 §13.2.2 via `HttpConditionalRequest`). |
| Ranges | `Accept-Ranges: bytes`; single satisfiable byte range → `206` + `Content-Range`; multi-range set → full `200` fallback; unsatisfiable → `416` + `bytes */N` (via `HttpRangeSelector`); `If-Range` gates application. |
| Content types | Extension lookup via `HttpContentTypes` with builder-time overlays; unmapped extensions pass through by default or serve the configured fallback type. |
| Precompression | On-disk `name.ext.br` / `name.ext.gz` siblings negotiate against `Accept-Encoding` (server prefers `br`); served with the logical file's `Content-Type`, the sibling's bytes/length/validators, `Content-Encoding`, and `Vary: Accept-Encoding` (emitted whenever a sibling exists, including on identity responses). |
| Default documents | Directory requests probe the configured names in order; a slash-less directory URL is `301`-redirected to its canonical slash form first. |
| HEAD | Same header section as `GET` (including `Content-Length`), no body. |

## Dependencies

- `Assimalign.Cohesion.Web` — pipeline contracts (`IWebApplicationPipelineBuilder`,
  `IWebApplicationMiddleware`).
- `Assimalign.Cohesion.Http` — the protocol primitives listed above.
- `Assimalign.Cohesion.FileSystem` — the content-root abstraction.

Per the Web-area hosting-isolation rule this package never references `Web.Hosting`;
applications receive it through the `App.Web` shared framework (`Sdk.Web`).

## Deferred follow-ups

Directory browsing and fingerprinted-asset manifests are deliberately out of scope (the
latter waits on endpoint routing, #28). See `DESIGN.md` for the reasoning and for the
HTTP/1.1 percent-decode parity note.
