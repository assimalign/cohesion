using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.StaticFiles;

/// <summary>
/// Builder-time configuration for <c>UseStaticFiles</c>. All values are snapshotted when the
/// middleware is composed — mutating an options instance after registration has no effect, and
/// nothing is resolved or re-read per request.
/// </summary>
public sealed class StaticFilesOptions
{
    /// <summary>
    /// Gets or sets the request-path prefix the middleware serves under. Requests whose path does
    /// not begin with this prefix (segment-aligned, ordinal comparison) pass through to the next
    /// middleware. The default is <c>/</c>, which serves the mounted file system at the site root.
    /// </summary>
    public HttpPath RequestPath { get; set; } = HttpPath.Root;

    /// <summary>
    /// Gets the default-document names probed, in order, when a request maps to a directory. The
    /// first name that exists as a file in that directory is served; an empty list disables
    /// default documents entirely. Names must be bare file names (no path separators). The
    /// defaults are <c>index.html</c> then <c>index.htm</c>.
    /// </summary>
    public IList<string> DefaultDocuments { get; } = new List<string> { "index.html", "index.htm" };

    /// <summary>
    /// Gets or sets the <c>Cache-Control</c> field value emitted on every served response
    /// (<c>200</c>/<c>206</c>/<c>304</c>/<c>416</c>), for example
    /// <c>public, max-age=3600, immutable</c>. <see langword="null"/> or empty emits no
    /// <c>Cache-Control</c> header. The value is validated against the RFC 9111 grammar when the
    /// middleware is composed.
    /// </summary>
    public string? CacheControl { get; set; }

    /// <summary>
    /// Gets the extension-to-content-type overrides overlaid onto
    /// <see cref="HttpContentTypes.Default"/> when the middleware is composed. Keys may be given
    /// with or without a leading dot and are case-insensitive; a key matching a default extension
    /// replaces the default value.
    /// </summary>
    public IDictionary<string, string> ContentTypeMappings { get; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets a value indicating whether files whose extension has no content-type mapping
    /// are served with <see cref="FallbackContentType"/>. When <see langword="false"/> (the
    /// default) such requests pass through to the next middleware — unknown types are blocked
    /// rather than guessed, so the application decides what owns them.
    /// </summary>
    public bool ServeUnknownContentTypes { get; set; }

    /// <summary>
    /// Gets or sets the content type used for unmapped extensions when
    /// <see cref="ServeUnknownContentTypes"/> is enabled. The default is
    /// <c>application/octet-stream</c>.
    /// </summary>
    public string FallbackContentType { get; set; } = HttpContentTypes.Fallback;

    /// <summary>
    /// Gets or sets a value indicating whether precompressed sibling assets are served. When a
    /// resolved file has an on-disk sibling with a <c>.br</c> or <c>.gz</c> suffix appended (for
    /// example <c>app.js.br</c> beside <c>app.js</c>), the response is negotiated against the
    /// request's <c>Accept-Encoding</c>: the sibling's bytes are sent with the original file's
    /// content type, the matching <c>Content-Encoding</c>, and <c>Vary: Accept-Encoding</c>.
    /// Enabled by default.
    /// </summary>
    public bool ServePrecompressedAssets { get; set; } = true;
}
