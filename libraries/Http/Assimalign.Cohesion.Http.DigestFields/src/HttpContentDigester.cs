using System;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Computes one or more RFC 9530 digests incrementally as content is written, then produces the
/// <see cref="HttpDigestField"/> for it. This is the "hash as you write" primitive behind
/// streamed-response digest stamping: a server feeds each body chunk to <see cref="Append"/> as it
/// writes it to the wire, and calls <see cref="ToField"/> once the body is complete to obtain the
/// field value to emit — for a streamed body, in the trailer section (RFC 9530 permits digest
/// fields as trailers, and <see cref="HttpFieldRules.IsProhibitedInTrailers"/> does not reject
/// them).
/// </summary>
/// <remarks>
/// Backed by the BCL <see cref="IncrementalHash"/> per algorithm, so no full-body buffer is
/// required and the type stays AOT-safe. <see cref="ToField"/> snapshots the digest without
/// resetting, so it may be called at the end of the body (the normal case) without preventing
/// further appends. The instance owns its hashes and must be disposed.
/// </remarks>
public sealed class HttpContentDigester : IDisposable
{
    private readonly HttpDigestAlgorithm[] _algorithms;
    private readonly IncrementalHash[] _hashes;
    private bool _disposed;

    /// <summary>
    /// Initializes a digester for the given algorithms.
    /// </summary>
    /// <param name="algorithms">The algorithms to compute; each must be supported.</param>
    /// <exception cref="ArgumentNullException"><paramref name="algorithms"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="algorithms"/> is empty or contains an unsupported algorithm.</exception>
    public HttpContentDigester(params HttpDigestAlgorithm[] algorithms)
    {
        ArgumentNullException.ThrowIfNull(algorithms);
        if (algorithms.Length == 0)
        {
            throw new ArgumentException("At least one algorithm is required.", nameof(algorithms));
        }

        _algorithms = new HttpDigestAlgorithm[algorithms.Length];
        _hashes = new IncrementalHash[algorithms.Length];
        for (int i = 0; i < algorithms.Length; i++)
        {
            HttpDigestAlgorithm algorithm = algorithms[i];
            if (!algorithm.IsSupported)
            {
                // Dispose the hashes already created before failing.
                for (int j = 0; j < i; j++)
                {
                    _hashes[j].Dispose();
                }
                throw new ArgumentException(
                    $"The digest algorithm '{algorithm}' cannot be used to generate a digest (RFC 9530 §5).", nameof(algorithms));
            }
            _algorithms[i] = algorithm;
            _hashes[i] = algorithm.CreateIncrementalHash();
        }
    }

    /// <summary>
    /// Feeds the next span of content to every algorithm's running hash.
    /// </summary>
    /// <param name="content">The content chunk written to the body.</param>
    /// <exception cref="ObjectDisposedException">The digester has been disposed.</exception>
    public void Append(ReadOnlySpan<byte> content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (IncrementalHash hash in _hashes)
        {
            hash.AppendData(content);
        }
    }

    /// <summary>
    /// Produces the digest field for the content appended so far, without resetting the running
    /// hashes.
    /// </summary>
    /// <returns>The digest field carrying one entry per configured algorithm.</returns>
    /// <exception cref="ObjectDisposedException">The digester has been disposed.</exception>
    public HttpDigestField ToField()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entries = new HttpDigestEntry[_algorithms.Length];
        Span<byte> buffer = stackalloc byte[64]; // large enough for SHA-512
        for (int i = 0; i < _algorithms.Length; i++)
        {
            int written = _hashes[i].GetCurrentHash(buffer);
            entries[i] = new HttpDigestEntry(_algorithms[i], buffer[..written].ToArray());
        }
        return HttpDigestField.FromEntries(entries);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (IncrementalHash hash in _hashes)
        {
            hash.Dispose();
        }
    }
}
