using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Streaming reader for <c>multipart/form-data</c> bodies. Reads one section
/// at a time, exposing the section headers + body stream without buffering
/// the entire request body in memory.
/// </summary>
/// <remarks>
/// <para>
/// Ported from ASP.NET Core's
/// <c>Microsoft.AspNetCore.WebUtilities.MultipartReader</c>. The typical
/// caller loop is:
/// </para>
/// <code>
/// MultipartReader reader = new(boundary, request.Body);
/// while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
/// {
///     // inspect section.Headers, copy section.Body
/// }
/// </code>
/// <para>
/// The reader honours per-section limits exposed through
/// <see cref="HeadersCountLimit"/>, <see cref="HeadersLengthLimit"/>, and
/// <see cref="BodyLengthLimit"/>. Limits default to values that match the
/// ASP.NET Core <c>FormOptions</c> defaults.
/// </para>
/// </remarks>
internal sealed class HttpMultipartFormReader
{
    /// <summary>Default cap on the number of headers per multipart section.</summary>
    public const int DefaultHeadersCountLimit = 16;

    /// <summary>Default cap on the total bytes in a single section's header block.</summary>
    public const int DefaultHeadersLengthLimit = 16 * 1024;

    private const int DefaultBufferSize = 4 * 1024;

    private readonly BufferedReadStream _stream;
    private readonly HttpMultipartFormBoundary _boundary;
    private HttpMultipartFormReaderStream? _currentSection;
    private bool _openingBoundaryConsumed;

    /// <summary>
    /// Initializes a reader over <paramref name="stream"/> using the supplied
    /// <paramref name="boundary"/> (the bare boundary value, without the
    /// <c>--</c> prefix). The first <c>ReadNextSectionAsync</c> call drains
    /// the preamble and the opening boundary.
    /// </summary>
    public HttpMultipartFormReader(string boundary, Stream stream)
        : this(boundary, stream, DefaultBufferSize)
    {
    }

    /// <summary>
    /// Initializes a reader with a custom buffer size; the buffer must be at
    /// least <c>boundary.Length + 2 (for "--") + 2 (for trailing "--") + 2
    /// (for CRLF)</c> bytes to handle worst-case boundary look-ahead.
    /// </summary>
    public HttpMultipartFormReader(string boundary, Stream stream, int bufferSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(boundary);
        ArgumentNullException.ThrowIfNull(stream);

        // Boundary length budget: leading "\r\n--" + boundary + trailing "--"
        // plus the CRLF that follows. 8 bytes of slack.
        int minBuffer = boundary.Length + 10;
        if (bufferSize < minBuffer)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize), $"Buffer must be at least {minBuffer} bytes to accommodate the boundary.");
        }

        _stream = new BufferedReadStream(stream, bufferSize);
        // The opening boundary is consumed via line-based reading; the
        // boundary's byte pattern (used by the in-body scanner) always
        // carries the leading CRLF.
        _boundary = new HttpMultipartFormBoundary(boundary);
    }

    /// <summary>Per-section cap on the number of headers (default <see cref="DefaultHeadersCountLimit"/>).</summary>
    public int HeadersCountLimit { get; set; } = DefaultHeadersCountLimit;

    /// <summary>Per-section cap on the total bytes in the header block (default <see cref="DefaultHeadersLengthLimit"/>).</summary>
    public int HeadersLengthLimit { get; set; } = DefaultHeadersLengthLimit;

    /// <summary>Per-section cap on body bytes. Null disables the cap.</summary>
    public long? BodyLengthLimit { get; set; }

    /// <summary>
    /// Reads and returns the next section, or <see langword="null"/> when
    /// the closing delimiter has been observed.
    /// </summary>
    public async Task<HttpMultipartFormSection?> ReadNextSectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Drain any leftover body from the previous section so the wire
        // cursor lands on the next boundary delimiter.
        if (_currentSection is not null)
        {
            await DrainCurrentSectionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_boundary.FinalBoundaryFound)
        {
            return null;
        }

        // First call: drain the preamble and consume the opening boundary
        // line ("--{boundary}\r\n"). Subsequent calls land here with the
        // cursor already past the previous section's closing delimiter
        // (the section body stream consumes the delimiter and the trailing
        // CRLF), so the next bytes are already the new section's headers.
        if (!_openingBoundaryConsumed)
        {
            if (!await ConsumeOpeningBoundaryAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            _openingBoundaryConsumed = true;
        }

        IReadOnlyDictionary<string, string> headers = await ReadHeadersAsync(cancellationToken).ConfigureAwait(false);
        HttpMultipartFormReaderStream sectionBody = new(_stream, _boundary)
        {
            LengthLimit = BodyLengthLimit,
        };

        _currentSection = sectionBody;
        return new HttpMultipartFormSection(headers, sectionBody);
    }

    private async Task<bool> ConsumeOpeningBoundaryAsync(CancellationToken cancellationToken)
    {
        // Scan forward, line by line, until we find the opening "--boundary".
        // The preamble (everything before the first boundary) is discarded.
        int totalRead = 0;
        while (true)
        {
            string line = await _stream.ReadLineAsync(HeadersLengthLimit, cancellationToken).ConfigureAwait(false);
            totalRead += line.Length + 2;

            if (totalRead > HeadersLengthLimit * 2)
            {
                // Defensive: a peer that just emits non-boundary lines forever
                // should not be allowed to drain the buffer indefinitely.
                throw new InvalidDataException("Multipart preamble exceeded the headers-length limit.");
            }

            if (line.Length == 0)
            {
                continue;
            }

            // Closing delimiter encountered as the opening line? That's a
            // zero-section body — done.
            if (line == "--" + _boundary.Boundary + "--")
            {
                _boundary.FinalBoundaryFound = true;
                return false;
            }

            if (line == "--" + _boundary.Boundary)
            {
                return true;
            }
        }
    }

    private async Task DrainCurrentSectionAsync(CancellationToken cancellationToken)
    {
        HttpMultipartFormReaderStream section = _currentSection!;
        if (section.FinishedSection)
        {
            _currentSection = null;
            return;
        }

        byte[] drain = new byte[4096];
        while (await section.ReadAsync(drain.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
        {
            // discard
        }

        _currentSection = null;
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadHeadersAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        int totalBytes = 0;

        while (true)
        {
            string line = await _stream.ReadLineAsync(HeadersLengthLimit - totalBytes, cancellationToken).ConfigureAwait(false);
            totalBytes += line.Length + 2; // CRLF

            if (line.Length == 0)
            {
                return headers;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new InvalidDataException($"Invalid multipart header line '{line}'.");
            }

            if (headers.Count >= HeadersCountLimit)
            {
                throw new InvalidDataException($"Multipart section header count exceeded the {HeadersCountLimit} limit.");
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();

            if (headers.TryGetValue(name, out string? existing))
            {
                // RFC 7230 §3.2.2 — combine repeated headers into one value
                // separated by ", ".
                headers[name] = existing + ", " + value;
            }
            else
            {
                headers[name] = value;
            }
        }
    }
}
