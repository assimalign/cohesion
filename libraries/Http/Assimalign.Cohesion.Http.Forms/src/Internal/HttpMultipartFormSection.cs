using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Represents a single section produced by <see cref="HttpMultipartFormReader"/> on
/// the wire. Exposes the section headers and a body stream limited to the
/// bytes belonging to the section &#8211; the underlying multipart boundary
/// is not visible through <see cref="Body"/>.
/// </summary>
/// <remarks>
/// Ported from ASP.NET Core's
/// <c>Microsoft.AspNetCore.WebUtilities.MultipartSection</c>. The
/// <see cref="Body"/> stream is single-use; reading past its end yields zero
/// and advances the parent reader to the next section.
/// </remarks>
internal sealed class HttpMultipartFormSection
{
    /// <summary>
    /// Initializes a section with the supplied headers and body.
    /// </summary>
    public HttpMultipartFormSection(IReadOnlyDictionary<string, string> headers, Stream body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(body);
        Headers = headers;
        Body = body;
    }

    /// <summary>Per-section headers, comma-folded into a single string per key.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Stream over the section body bytes. Single-use.</summary>
    public Stream Body { get; }

    /// <summary>
    /// Convenience accessor for the section's <c>Content-Type</c> header, or
    /// <see langword="null"/> when absent.
    /// </summary>
    public string? ContentType => Headers.TryGetValue("Content-Type", out string? value) ? value : null;

    /// <summary>
    /// Convenience accessor for the section's <c>Content-Disposition</c>
    /// header, or <see langword="null"/> when absent.
    /// </summary>
    public string? ContentDisposition => Headers.TryGetValue("Content-Disposition", out string? value) ? value : null;
}
