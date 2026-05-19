using Assimalign.Cohesion.Http.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpFormFeature"/> implementation. Parses request bodies
/// in <c>application/x-www-form-urlencoded</c> and <c>multipart/form-data</c>
/// flavours via the streaming <see cref="HttpFormReader"/> and
/// <see cref="HttpMultipartFormReader"/> ports, caches the result for subsequent
/// reads, and throws when individual headers / values exceed the configured
/// <see cref="HttpFormOptions"/> limits.
/// </summary>
/// <remarks>
/// <para>
/// Bodies are read incrementally rather than buffered in full: the
/// urlencoded path streams through <see cref="HttpFormReader"/> a chunk at a
/// time, and the multipart path uses <see cref="HttpMultipartFormReader"/> with a
/// per-section stream. Multipart files larger than
/// <see cref="HttpFormOptions.MemoryBufferThreshold"/> spill to a temp file
/// to keep peak memory bounded.
/// </para>
/// <para>
/// Cohesion's <see cref="IHttpFormCollection"/> stores one value per key
/// (single <see cref="HttpQueryValue"/>); when a urlencoded body sends the
/// same key multiple times, the values are comma-joined per RFC 7230 §3.2.2
/// before being added to the collection. Multi-value-aware form access is a
/// future enhancement once the form-collection API grows a multi-value
/// indexer.
/// </para>
/// </remarks>
public sealed class HttpFormFeature : IHttpFormFeature
{
    private const string UrlEncodedContentType = "application/x-www-form-urlencoded";
    private const string MultipartContentType = "multipart/form-data";
    private const string MultipartBoundaryParameter = "boundary=";

    private readonly IHttpRequest? _request;
    private readonly HttpFormOptions _options;
    private IHttpFormCollection? _form;
    private Task<IHttpFormCollection>? _parsedFormTask;

    /// <summary>
    /// Initializes an empty feature. <see cref="ReadFormAsync"/> returns an
    /// empty collection.
    /// </summary>
    public HttpFormFeature()
        : this(form: null, request: null, options: HttpFormOptions.Default)
    {
    }

    /// <summary>
    /// Initializes the feature with a pre-attached form collection.
    /// <see cref="ReadFormAsync"/> short-circuits and returns
    /// <paramref name="form"/> without touching any request body.
    /// </summary>
    /// <param name="form">The pre-parsed form to expose.</param>
    public HttpFormFeature(IHttpFormCollection form)
        : this(form: form, request: null, options: HttpFormOptions.Default)
    {
    }

    /// <summary>
    /// Initializes the feature for lazy parsing against
    /// <paramref name="request"/> using <see cref="HttpFormOptions.Default"/>.
    /// </summary>
    /// <param name="request">The request whose body backs the form parse.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public HttpFormFeature(IHttpRequest request)
        : this(request, HttpFormOptions.Default)
    {
    }

    /// <summary>
    /// Initializes the feature for lazy parsing against
    /// <paramref name="request"/> with the supplied limits.
    /// </summary>
    /// <param name="request">The request whose body backs the form parse.</param>
    /// <param name="options">Per-section / per-value limits.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public HttpFormFeature(IHttpRequest request, HttpFormOptions options)
        : this(form: null, request: request, options: options)
    {
        ArgumentNullException.ThrowIfNull(request);
    }

    private HttpFormFeature(IHttpFormCollection? form, IHttpRequest? request, HttpFormOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _form = form;
        _request = request;
        _options = options;
    }

    /// <inheritdoc />
    public string Name => nameof(IHttpFormFeature);

    /// <inheritdoc />
    public IHttpFormCollection? Form => _form;

    /// <inheritdoc />
    public Task<IHttpFormCollection> ReadFormAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_form is not null)
        {
            return Task.FromResult(_form);
        }

        if (_request is null)
        {
            // No request to parse against — install an empty collection and cache it.
            _form = new HttpFormCollection();
            return Task.FromResult(_form);
        }

        // Memoize the in-flight parse Task so concurrent ReadFormAsync calls
        // don't each kick off a separate read against the same body stream.
        return _parsedFormTask ??= ParseAsync(cancellationToken);
    }

    private async Task<IHttpFormCollection> ParseAsync(CancellationToken cancellationToken)
    {
        IHttpRequest request = _request!;
        string contentType = GetContentType(request);
        HttpFormCollection form = new();

        if (string.IsNullOrWhiteSpace(contentType))
        {
            _form = form;
            return form;
        }

        if (IsUrlEncoded(contentType))
        {
            await ReadUrlEncodedAsync(request.Body, form, cancellationToken).ConfigureAwait(false);
        }
        else if (TryGetMultipartBoundary(contentType, out string boundary))
        {
            await ReadMultipartAsync(request.Body, boundary, form, cancellationToken).ConfigureAwait(false);
        }
        // Unknown content type — leave the empty collection. Callers that
        // want to handle non-form bodies should reach for the raw request
        // body directly.

        _form = form;
        return form;
    }

    private async Task ReadUrlEncodedAsync(Stream body, HttpFormCollection form, CancellationToken cancellationToken)
    {
        using HttpFormReader reader = new(body, Encoding.UTF8)
        {
            ValueCountLimit = _options.ValueCountLimit,
            KeyLengthLimit = _options.KeyLengthLimit,
            ValueLengthLimit = _options.ValueLengthLimit,
        };

        Dictionary<string, List<string>> parsed = await reader.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        foreach (KeyValuePair<string, List<string>> pair in parsed)
        {
            // RFC 7230 §3.2.2 — comma-join repeated values into a single
            // representation. Cohesion's HttpFormCollection stores one
            // HttpQueryValue per key; a multi-value indexer is a future
            // enhancement.
            string value = pair.Value.Count switch
            {
                0 => string.Empty,
                1 => pair.Value[0],
                _ => string.Join(",", pair.Value),
            };
            form.Add(pair.Key, new HttpQueryValue(value));
        }
    }

    private async Task ReadMultipartAsync(Stream body, string boundary, HttpFormCollection form, CancellationToken cancellationToken)
    {
        HttpMultipartFormReader reader = new(boundary, body)
        {
            HeadersCountLimit = _options.MultipartHeadersCountLimit,
            HeadersLengthLimit = _options.MultipartHeadersLengthLimit,
            BodyLengthLimit = _options.MultipartBodyLengthLimit,
        };

        while (await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false) is { } section)
        {
            ParseContentDisposition(section.ContentDisposition, out string? name, out string? fileName);

            if (string.IsNullOrEmpty(name))
            {
                // RFC 7578 §4.2 — Content-Disposition with a non-empty name
                // is required. Skip malformed parts rather than throwing so a
                // single bad part doesn't poison the whole form.
                await DrainAsync(section.Body, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (fileName is not null)
            {
                HttpFormFile file = await ReadFileSectionAsync(section, name, fileName, cancellationToken).ConfigureAwait(false);
                form.Add(file);
            }
            else
            {
                string value = await ReadValueSectionAsync(section, cancellationToken).ConfigureAwait(false);
                form.Add(name, new HttpQueryValue(value));
            }
        }
    }

    private static async Task<string> ReadValueSectionAsync(HttpMultipartFormSection section, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        await section.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private async Task<HttpFormFile> ReadFileSectionAsync(HttpMultipartFormSection section, string name, string fileName, CancellationToken cancellationToken)
    {
        // Spill-to-disk: small uploads stay in memory; larger ones flush to a
        // temp file once the in-memory buffer crosses MemoryBufferThreshold.
        // The temp file is materialized only when needed; the resulting
        // HttpFormFile owns the temp-file lifetime through its stream factory.
        MemoryStream memory = new();
        byte[] chunk = new byte[81 * 1024];
        long total = 0;
        string? spillPath = null;
        FileStream? spillFile = null;

        try
        {
            while (true)
            {
                int read = await section.Body.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (spillFile is not null)
                {
                    await spillFile.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                else if (total > _options.MemoryBufferThreshold)
                {
                    // Cross-over: switch to disk and flush what we've buffered.
                    spillPath = Path.Combine(Path.GetTempPath(), $"cohesion-form-{Guid.NewGuid():N}.tmp");
                    spillFile = new FileStream(spillPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, bufferSize: 4096, FileOptions.DeleteOnClose);
                    memory.Position = 0;
                    await memory.CopyToAsync(spillFile, cancellationToken).ConfigureAwait(false);
                    await spillFile.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    memory.Dispose();
                }
                else
                {
                    memory.Write(chunk, 0, read);
                }
            }

            string? contentType = section.ContentType;

            if (spillFile is not null)
            {
                // Detach the spill file from the disposing scope; the
                // FormFile's stream factory reopens the temp file on demand.
                // We rely on FileOptions.DeleteOnClose to clean up when the
                // OS finally closes the last handle.
                string capturedPath = spillPath!;
                spillFile.Dispose();
                return new HttpFormFile(
                    name,
                    fileName,
                    () => new FileStream(capturedPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete),
                    total,
                    contentType);
            }
            else
            {
                byte[] inMemoryBytes = memory.ToArray();
                memory.Dispose();
                return new HttpFormFile(
                    name,
                    fileName,
                    () => new MemoryStream(inMemoryBytes, writable: false),
                    total,
                    contentType);
            }
        }
        catch
        {
            spillFile?.Dispose();
            memory.Dispose();
            throw;
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[4096];
        while (await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
        {
            // discard
        }
    }

    private static string GetContentType(IHttpRequest request)
    {
        return request.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue value)
            ? value.Value
            : string.Empty;
    }

    private static bool IsUrlEncoded(string contentType)
    {
        return contentType.StartsWith(UrlEncodedContentType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetMultipartBoundary(string contentType, out string boundary)
    {
        boundary = string.Empty;

        if (!contentType.StartsWith(MultipartContentType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int idx = contentType.IndexOf(MultipartBoundaryParameter, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        string remainder = contentType[(idx + MultipartBoundaryParameter.Length)..].Trim();
        int semi = remainder.IndexOf(';');
        if (semi >= 0)
        {
            remainder = remainder[..semi].Trim();
        }

        // RFC 2046 §5.1.1 — boundary parameter values MAY be DQUOTE-wrapped.
        if (remainder.Length >= 2 && remainder[0] == '"' && remainder[^1] == '"')
        {
            remainder = remainder[1..^1];
        }

        boundary = remainder;
        return remainder.Length > 0;
    }

    private static void ParseContentDisposition(string? value, out string? name, out string? fileName)
    {
        name = null;
        fileName = null;

        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (string segment in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = segment.Trim();

            if (TryGetParameter(trimmed, "name", out string? n))
            {
                name = n;
            }
            else if (TryGetParameter(trimmed, "filename", out string? f))
            {
                fileName = f;
            }
        }
    }

    private static bool TryGetParameter(string segment, string parameter, out string? value)
    {
        value = null;

        if (segment.Length <= parameter.Length ||
            !segment.StartsWith(parameter, StringComparison.OrdinalIgnoreCase) ||
            segment[parameter.Length] != '=')
        {
            return false;
        }

        string raw = segment[(parameter.Length + 1)..].Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            raw = raw[1..^1];
        }

        value = raw;
        return true;
    }
}