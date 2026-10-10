using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.StaticFiles.Internal;

/// <summary>
/// Serves <c>GET</c>/<c>HEAD</c> requests under a request-path prefix from a mounted
/// <see cref="IFileSystem"/>. This type resolves the request to a file — the traversal gate,
/// default documents, content types via <see cref="HttpContentTypes"/>, and <c>Accept-Encoding</c>
/// negotiation of precompressed siblings via <see cref="HttpContentNegotiation"/> — and hands the
/// selected file to <see cref="RepresentationWriter"/>, which applies RFC 9110 &#167; 13
/// preconditions via <see cref="HttpConditionalRequest"/> and &#167; 14 byte ranges via
/// <see cref="HttpRangeSelector"/> and writes the response. All composition state is frozen at
/// construction — no request-time service location, no per-request allocation beyond the exchange
/// itself.
/// </summary>
internal sealed class StaticFilesMiddleware : IWebApplicationMiddleware
{
    // Precompressed sibling codings in server preference order (RFC 9110 §12.5.3 lets the
    // server break client ties): brotli first, then gzip.
    private static readonly (string Coding, string Suffix)[] _precompressedCodings =
    [
        ("br", ".br"),
        ("gzip", ".gz"),
    ];

    private readonly IFileSystem _fileSystem;
    private readonly string _prefix;
    private readonly string[] _defaultDocuments;
    private readonly string? _cacheControl;
    private readonly FrozenDictionary<string, string> _contentTypes;
    private readonly bool _serveUnknownContentTypes;
    private readonly string _fallbackContentType;
    private readonly bool _servePrecompressedAssets;

    public StaticFilesMiddleware(IFileSystem fileSystem, StaticFilesOptions options)
    {
        _fileSystem = fileSystem;
        // "/" mounts at the site root (empty prefix); anything else is stored without a
        // trailing slash so segment-aligned matching stays uniform.
        _prefix = options.RequestPath.Value == "/" ? string.Empty : options.RequestPath.Value.TrimEnd('/');
        _defaultDocuments = [.. options.DefaultDocuments];
        _cacheControl = string.IsNullOrEmpty(options.CacheControl) ? null : options.CacheControl;
        _contentTypes = options.ContentTypeMappings.Count == 0
            ? HttpContentTypes.Default
            : HttpContentTypes.CreateMap(options.ContentTypeMappings);
        _serveUnknownContentTypes = options.ServeUnknownContentTypes;
        _fallbackContentType = options.FallbackContentType;
        _servePrecompressedAssets = options.ServePrecompressedAssets;
    }

    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        HttpMethod method = context.Request.Method;
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            await next.Invoke(context);
            return;
        }

        // Every transport now percent-decodes the request-target path before middleware sees it
        // (HttpPath.FromUriComponent in Http1MessageReader / Http2Stream / Http3HeaderCodec), so the
        // traversal gate and the file lookup see the same decoded text regardless of protocol — "%2e%2e"
        // arrives here as ".." on h1 exactly as it does on h2/h3. The middleware MUST NOT re-decode, or
        // an encoded octet would decode twice; it trusts the transport's single decode. Inside a
        // Map(path) branch the effective path is the path below the branch's prefix (#1056).
        await ServeAsync(context, context.GetEffectivePath().Value, next);
    }

    /// <summary>
    /// Serves the file <paramref name="path"/> names, relative to the mount's request-path prefix, or
    /// calls <paramref name="next"/> when this mount holds no servable file there.
    /// </summary>
    /// <param name="context">The exchange.</param>
    /// <param name="path">The decoded request path to resolve (the request's own, or a fallback's file).</param>
    /// <param name="next">The delegate invoked when no file is served.</param>
    /// <returns>A task that completes when the response (or <paramref name="next"/>) has completed.</returns>
    internal async Task ServeAsync(IHttpContext context, string path, WebApplicationMiddleware next)
    {
        if (!StaticFilePath.TryGetRelativePath(path, _prefix, out string remainder))
        {
            await next.Invoke(context);
            return;
        }

        // The traversal gate: under our prefix, a path with dot segments (or NUL/':', or an 8.3
        // short-name alias, whose extension is not the file's) is hostile or nonsensical — answer
        // 404 directly rather than letting it reach any resolver. See StaticFilePath.HasUnsafeSegments
        // for what the transports decode.
        if (StaticFilePath.HasUnsafeSegments(remainder))
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        // An exact-prefix match ("" remainder) is the mount root addressed without a trailing
        // slash; normalize the lookup to the file-system root and remember the slash for the
        // default-document redirect decision.
        bool hadTrailingSlash = remainder.Length > 0 && remainder[^1] == '/';
        string candidate = remainder.Length == 0 ? "/" : remainder;

        FileSystemPath filePath;
        try
        {
            filePath = FileSystemPath.Parse(candidate);
        }
        catch (ArgumentException)
        {
            // Second defense layer: FileSystemPath rejects interior dot segments and illegal
            // path characters outright. Anything it refuses is not a servable file.
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        // The mount root exists by definition; everything else must resolve through the mount.
        IFileSystemInfo? info = null;
        if (candidate != "/" && !TryGetInfo(filePath, out info))
        {
            await next.Invoke(context);
            return;
        }

        string logicalName;
        IFileSystemFile file;

        // Directory detection is by info type, not FileAttributes: not every mount stamps
        // FileAttributes.Directory (InMemory leaves attributes unset), but every mount returns
        // an IFileSystemDirectory-typed info for a directory.
        if (info is null || info is IFileSystemDirectory)
        {
            if (!TryResolveDefaultDocument(candidate, out IFileSystemFile? document, out string documentName))
            {
                // No default document (or none configured): directory browsing is a deferred
                // follow-up, so the directory itself is not servable.
                await next.Invoke(context);
                return;
            }

            if (!hadTrailingSlash)
            {
                // Serving directory content at the slash-less URL would break every relative
                // link inside the document, so canonicalize first (RFC 9110 §15.4.2).
                RedirectAppendingSlash(context);
                return;
            }

            file = document;
            logicalName = documentName;
        }
        else
        {
            if (info is not IFileSystemFile resolved)
            {
                await next.Invoke(context);
                return;
            }

            file = resolved;
            logicalName = GetFileName(candidate);
        }

        // Content-type gate before any validator work: a file this middleware will not claim
        // should never emit validators or 304s. The logical (unencoded) name decides the type —
        // a precompressed sibling only changes the coding, never the media type. A name with no
        // extension ("html", ".json") has no type, so an upload named "html" is never served as
        // text/html: it passes through like any unmapped name.
        if (!HttpContentTypes.TryGetFromFileName(_contentTypes, logicalName, out string contentType))
        {
            if (!_serveUnknownContentTypes)
            {
                await next.Invoke(context);
                return;
            }
            contentType = _fallbackContentType;
        }

        // Negotiate the representation (identity vs precompressed sibling) before evaluating
        // preconditions: validators belong to the representation actually selected.
        IFileSystemFile servedFile = file;
        string? contentEncoding = null;
        bool varyByAcceptEncoding = false;

        if (_servePrecompressedAssets)
        {
            SelectPrecompressedSibling(context, candidate, ref servedFile, ref contentEncoding, ref varyByAcceptEncoding);
        }

        // Validators, preconditions, ranges, and the response itself are the shared representation
        // engine's — the same code the SendFileAsync response helpers run. The served file's own
        // metadata decides them, so a precompressed sibling carries its own length and ETag.
        RepresentationMetadata presentation = new()
        {
            ContentType = contentType,
            ContentEncoding = contentEncoding,
            CacheControl = _cacheControl,
            VaryByAcceptEncoding = varyByAcceptEncoding,
        };

        await RepresentationWriter.SendFileAsync(context, servedFile, presentation, context.RequestCancelled).ConfigureAwait(false);
    }

    private bool TryGetInfo(FileSystemPath path, [NotNullWhen(true)] out IFileSystemInfo? info)
        => StaticFilePath.TryGetInfo(_fileSystem, path, out info);

    private bool TryResolveDefaultDocument(string directory, [NotNullWhen(true)] out IFileSystemFile? document, out string documentName)
    {
        document = null;
        documentName = string.Empty;

        if (_defaultDocuments.Length == 0)
        {
            return false;
        }

        string directoryPrefix = directory == "/" ? "/" : directory.TrimEnd('/') + "/";
        foreach (string name in _defaultDocuments)
        {
            if (TryGetInfo(FileSystemPath.Parse(directoryPrefix + name), out IFileSystemInfo? info)
                && info is IFileSystemFile candidate)
            {
                document = candidate;
                documentName = name;
                return true;
            }
        }

        return false;
    }

    private void SelectPrecompressedSibling(
        IHttpContext context,
        string candidate,
        ref IFileSystemFile servedFile,
        ref string? contentEncoding,
        ref bool varyByAcceptEncoding)
    {
        Span<int> available = stackalloc int[_precompressedCodings.Length];
        int count = 0;
        for (int i = 0; i < _precompressedCodings.Length; i++)
        {
            if (TryGetInfo(FileSystemPath.Parse(candidate + _precompressedCodings[i].Suffix), out IFileSystemInfo? info)
                && info is IFileSystemFile)
            {
                available[count++] = i;
            }
        }

        if (count == 0)
        {
            return;
        }

        // The same URL can now yield different representations by Accept-Encoding, so every
        // response for it must carry Vary — including the identity one a non-accepting client gets.
        varyByAcceptEncoding = true;

        string[] serverCodings = new string[count];
        for (int i = 0; i < count; i++)
        {
            serverCodings[i] = _precompressedCodings[available[i]].Coding;
        }

        string? acceptEncoding = context.Request.Headers.TryGetValue(HttpHeaderKey.AcceptEncoding, out HttpHeaderValue acceptEncodingValue)
            ? (string?)acceptEncodingValue
            : null;

        // A false return means even identity was refused (identity;q=0). RFC 9110 §12.5.3 lets
        // the server answer 406 or ignore the field; a static asset server serves identity —
        // refusing cacheable bytes over a hostile-or-misconfigured header helps no one.
        if (!HttpContentNegotiation.TrySelectEncoding(acceptEncoding, serverCodings, out string selected)
            || selected == "identity")
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            (string coding, string suffix) = _precompressedCodings[available[i]];
            if (coding == selected
                && TryGetInfo(FileSystemPath.Parse(candidate + suffix), out IFileSystemInfo? sibling)
                && sibling is IFileSystemFile siblingFile)
            {
                servedFile = siblingFile;
                contentEncoding = coding;
                return;
            }
        }
    }

    private static void RedirectAppendingSlash(IHttpContext context)
    {
        // Reconstructing the query from the parsed collection re-encodes each part; a redirect
        // target needs semantic, not byte-for-byte, fidelity.
        string location = context.Request.Path.Value + "/";
        if (context.Request.Query.Count > 0)
        {
            var builder = new StringBuilder(location).Append('?');
            bool first = true;
            foreach (KeyValuePair<HttpQueryKey, HttpQueryValue> pair in context.Request.Query)
            {
                if (!first)
                {
                    builder.Append('&');
                }
                first = false;
                builder.Append(Uri.EscapeDataString(pair.Key.ToString())).Append('=').Append(Uri.EscapeDataString(pair.Value.ToString()));
            }
            location = builder.ToString();
        }

        context.Response.StatusCode = HttpStatusCode.MovedPermanently;
        context.Response.Headers[HttpHeaderKey.Location] = location;
    }

    private static string GetFileName(string candidate)
    {
        int separator = candidate.LastIndexOf('/');
        return separator < 0 ? candidate : candidate[(separator + 1)..];
    }
}
