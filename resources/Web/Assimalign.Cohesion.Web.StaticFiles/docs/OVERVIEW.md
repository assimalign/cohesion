# Assimalign.Cohesion.Web.StaticFiles — Overview

Static file serving for the Cohesion Web pipeline, built over the `libraries/FileSystem`
abstractions. One feature package covers file serving, default documents, conditional GET,
single byte-range responses, content-type mapping, and precompressed (`.br`/`.gz`) sibling
negotiation — composed from the shared `Assimalign.Cohesion.Http` protocol primitives rather
than re-deriving any RFC semantics locally. Handlers get the same behavior for a file or a stream
of their own through the `SendFileAsync` and `WriteStreamAsync` response helpers.

## Scope

- **Content root = an `IFileSystem` mount.** Any implementation works: `PhysicalFileSystem`,
  `InMemoryFileSystem`, `AggregateFileSystem` composites. The middleware can never address
  anything outside the mount — path-traversal defense is a hard guarantee, not an option.
- **`GET`/`HEAD` only.** Everything else passes through to the next middleware.
- **Builder-time composition only.** The file system and every option are supplied at
  `UseStaticFiles(...)` and frozen into the middleware; there is no service container, no
  configuration binding, and no request-time service location (the Web-area rule).

## Usage

The parameterless verb serves the application's web root: `wwwroot` under the content root
(`IWebApplicationContext.WebRootPath`, set by the hosting runtime). It never serves the content
root itself or the working directory, and it passes every request through when the application
has no web root:

```csharp
app.UseStaticFiles();                                  // serves <content root>/wwwroot
app.UseStaticFiles(options => options.CacheControl = "public, max-age=3600");
```

Mount any other file system explicitly:

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
    options.ServeUnknownContentTypes = false;          // default: unmapped or missing extensions pass through
    options.ServePrecompressedAssets = true;           // default: .br/.gz siblings negotiate
});
```

A `ContentTypeMappings` key maps an extension, never a whole file name, so no key types a file
without one, such as `apple-app-site-association`. Give such files a mount of their own whose
fallback type is theirs, or send them from a handler with an explicit type:

```csharp
app.UseStaticFiles(wellKnownRoot, options =>         // holds only the extensionless files
{
    options.RequestPath = new HttpPath("/.well-known");
    options.ServeUnknownContentTypes = true;
    options.FallbackContentType = "application/json";
});

// or: context.Response.SendFileAsync(wellKnownRoot, name, "application/json")
```

Request paths whose segments look like Windows 8.3 short names (`UPLOAD~1.HTM`) answer `404`: an
alias would open `upload.htmlx` and type it from the alias's extension.

A single-page application serves its assets first and answers every client-side route with
`index.html`. The fallback never answers a file-name path, so a missing asset stays a 404:

```csharp
app.UseStaticFiles();                  // existing assets
app.UseRouting();                      // API routes
app.MapGet("/api/orders", ...);
app.MapFallbackToFile("index.html");   // everything else that is not a file
```

Static files can also be mounted in a path branch, which serves below the branch's prefix:
`app.Map("/static", branch => branch.UseStaticFiles())`.

## Sending a file or a stream from a handler

`HttpResponseFileExtensions` adds three members to `IHttpResponse`. They answer a request the way
the middleware answers it — validators, conditional requests, single byte ranges, `HEAD` — because
they run the same engine:

```csharp
using Assimalign.Cohesion.Web.StaticFiles;

var reports = new PhysicalFileSystem(new PhysicalFileSystemOptions { Root = reportsRoot, IsReadOnly = true });

// A path inside a mount. Safe to build from a route value: dot segments, '\' traversal, drive and
// stream forms, NUL, and 8.3 short-name aliases are answered 404, and nothing outside the mount can
// be addressed.
app.MapGet("/reports/{name}", (string name, IHttpContext context)
    => context.Response.SendFileAsync(reports, name, cancellationToken: context.RequestCancelled));

// A file the handler already resolved, with an explicit content type.
await context.Response.SendFileAsync(file, "application/pdf");

// A stream: the caller supplies the validators; nothing is hashed.
await context.Response.WriteStreamAsync(blobStream, "video/mp4",
    entityTag: HttpEntityTag.Strong(blob.Version), lastModified: blob.UpdatedOn);
```

| Helper | Content type | Validators | Ranges |
|---|---|---|---|
| `SendFileAsync(IFileSystemFile, ...)` | Explicit, else from the file name; unmapped or no extension (`html`, `.json`) → `application/octet-stream` | Strong `ETag` from `Size` + `UpdatedOn`, `Last-Modified` — the same as `UseStaticFiles` | Single range → `206`; unsatisfiable → `416` |
| `SendFileAsync(IFileSystem, path, ...)` | As above, for the resolved file | As above | As above; an unsafe, missing, or directory path → `404` |
| `WriteStreamAsync(Stream, ...)` | Explicit, else `application/octet-stream` | Only the `entityTag`/`lastModified` the caller passes | Seekable stream only; a non-seekable stream is sent whole, without `Content-Length` |

The representation a stream contributes is its remaining bytes, from the current position; the
stream is not disposed. Headers set before the call (`Cache-Control`, `Content-Disposition`) are kept
and also ride on a `304`. Pass an explicit content type for user-supplied files: a name such as
`avatar.html` would otherwise be served as `text/html`. The reasoning behind each choice is in
`DESIGN.md`, "Response helpers".

## What a served response carries

| Concern | Behavior |
|---|---|
| Validators | Strong `ETag` derived from `Size` + `UpdatedOn`; `Last-Modified` (HTTP-date). |
| Conditional GET | `If-None-Match` / `If-Modified-Since` → `304`; `If-Match` / `If-Unmodified-Since` → `412` (RFC 9110 §13.2.2 via `HttpConditionalRequest`). |
| Ranges | `Accept-Ranges: bytes`; single satisfiable byte range → `206` + `Content-Range`; multi-range set → full `200` fallback; unsatisfiable → `416` + `bytes */N` (via `HttpRangeSelector`); `If-Range` gates application. |
| Content types | File-name lookup via `HttpContentTypes` with builder-time overlays; a name whose extension is unmapped, or that has none (`html`, the dotfile `.json`), passes through by default or serves the configured fallback type. |
| Precompression | On-disk `name.ext.br` / `name.ext.gz` siblings negotiate against `Accept-Encoding` (server prefers `br`); served with the logical file's `Content-Type`, the sibling's bytes/length/validators, `Content-Encoding`, and `Vary: Accept-Encoding` (emitted whenever a sibling exists, including on identity responses). |
| Default documents | Directory requests probe the configured names in order; a slash-less directory URL is `301`-redirected to its canonical slash form first. |
| HEAD | Same header section as `GET` (including `Content-Length`), no body. |

The response helpers produce the same columns for a handler's file or stream, minus precompression
and default documents.

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
