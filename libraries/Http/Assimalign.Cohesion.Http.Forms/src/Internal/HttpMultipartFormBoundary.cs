using System;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Tracks the multipart boundary octets and the per-body "is this the
/// closing delimiter?" state.
/// </summary>
/// <remarks>
/// <para>
/// Ported from ASP.NET Core's
/// <c>Microsoft.AspNetCore.WebUtilities.HttpMultipartFormBoundary</c>. Holds the
/// boundary as both a string (for diagnostics) and as the prefixed byte
/// sequence (<c>"\r\n--{boundary}"</c>) the body scanner matches against.
/// </para>
/// <para>
/// The opening boundary is consumed by the top-level reader through
/// line-based reading. Once the body scanner takes over, every subsequent
/// boundary on the wire is preceded by CRLF (it terminates the previous
/// section's body), so the byte-pattern scanner only ever needs the
/// with-CRLF form.
/// </para>
/// </remarks>
internal sealed class HttpMultipartFormBoundary
{
    public HttpMultipartFormBoundary(string boundary)
    {
        ArgumentException.ThrowIfNullOrEmpty(boundary);

        Boundary = boundary;
        BoundaryBytes = System.Text.Encoding.UTF8.GetBytes("\r\n--" + boundary);
        FinalBoundaryLength = BoundaryBytes.Length + 2; // trailing "--"
    }

    public string Boundary { get; }

    /// <summary>Boundary octets the in-body scanner matches against
    /// (<c>"\r\n--{boundary}"</c>).</summary>
    public byte[] BoundaryBytes { get; }

    /// <summary>Length of the closing delimiter
    /// (<see cref="BoundaryBytes"/> suffixed with <c>--</c>).</summary>
    public int FinalBoundaryLength { get; }

    /// <summary>Set by the section reader when the closing delimiter is
    /// matched; subsequent <c>ReadNextSectionAsync</c> calls then return
    /// <see langword="null"/>.</summary>
    public bool FinalBoundaryFound { get; set; }
}
