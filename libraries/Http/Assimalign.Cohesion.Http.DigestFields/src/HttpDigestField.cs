using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The parsed value of an RFC 9530 <c>Content-Digest</c> or <c>Repr-Digest</c> field: an ordered
/// map from <see cref="HttpDigestAlgorithm"/> to the raw digest bytes computed for it. Both fields
/// share the identical RFC 9651 Structured Field Dictionary syntax
/// (<c>algorithm=:base64-digest:</c>), so one value model serves both; the distinction is only
/// which header name carries it and what the digest is taken over (message content versus
/// representation data).
/// </summary>
/// <remarks>
/// <para>
/// The field is a thin, allocation-light view over the parsed dictionary. Parsing preserves every
/// well-formed dictionary member — including deprecated and unregistered algorithms — so a
/// recipient can enumerate every offered digest; <see cref="Entries"/> surfaces the recognized
/// registry entries in field order, and verification silently skips any whose algorithm this
/// library cannot compute (RFC 9530 &#167; 5).
/// </para>
/// <para>
/// Hashing is always performed through the BCL <see cref="IncrementalHash"/> primitive, so the
/// type is AOT-safe: no reflection, no runtime code generation, no algorithm lookup by string into
/// a provider model.
/// </para>
/// </remarks>
public readonly struct HttpDigestField
{
    private readonly StructuredFieldDictionary _dictionary;
    private readonly HttpDigestEntry[]? _entries;

    private HttpDigestField(StructuredFieldDictionary dictionary, HttpDigestEntry[] entries)
    {
        _dictionary = dictionary;
        _entries = entries;
    }

    /// <summary>
    /// Gets the recognized RFC 9530 registry entries carried by the field, in field order. Members
    /// whose algorithm key is not in the registry are preserved for round-tripping (see
    /// <see cref="Serialize"/>) but are not surfaced here, because a recipient cannot act on an
    /// unknown algorithm.
    /// </summary>
    public IReadOnlyList<HttpDigestEntry> Entries => _entries ?? Array.Empty<HttpDigestEntry>();

    /// <summary>
    /// Gets a value indicating whether the field carries at least one entry whose algorithm this
    /// library can compute and verify with (<see cref="HttpDigestAlgorithm.IsSupported"/>).
    /// </summary>
    public bool HasSupportedAlgorithm
    {
        get
        {
            if (_entries is not null)
            {
                foreach (HttpDigestEntry entry in _entries)
                {
                    if (entry.Algorithm.IsSupported)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Attempts to get the digest bytes carried for <paramref name="algorithm"/>.
    /// </summary>
    /// <param name="algorithm">The algorithm to look up.</param>
    /// <param name="digest">When this method returns <see langword="true"/>, the raw digest bytes.</param>
    /// <returns><see langword="true"/> if the field carried a digest for the algorithm; otherwise <see langword="false"/>.</returns>
    public bool TryGetDigest(HttpDigestAlgorithm algorithm, out ReadOnlyMemory<byte> digest)
    {
        if (_entries is not null && algorithm.IsRegistered)
        {
            foreach (HttpDigestEntry entry in _entries)
            {
                if (entry.Algorithm == algorithm)
                {
                    digest = entry.Digest;
                    return true;
                }
            }
        }
        digest = default;
        return false;
    }

    #region Parse

    /// <summary>
    /// Parses a <c>Content-Digest</c> / <c>Repr-Digest</c> field value.
    /// </summary>
    /// <param name="value">The header field value; repeated field lines are combined by comma per RFC 9651 &#167; 4.2.</param>
    /// <param name="field">When this method returns <see langword="true"/>, the parsed field.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(HttpHeaderValue value, out HttpDigestField field)
        => TryParse(value.Value.AsSpan(), out field, out _);

    /// <summary>
    /// Parses a <c>Content-Digest</c> / <c>Repr-Digest</c> field value.
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <param name="field">When this method returns <see langword="true"/>, the parsed field.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> input, out HttpDigestField field)
        => TryParse(input, out field, out _);

    /// <summary>
    /// Parses a <c>Content-Digest</c> / <c>Repr-Digest</c> field value. On failure,
    /// <paramref name="error"/> carries a human-readable explanation (malformed dictionary syntax,
    /// a member that is not a Byte Sequence, or an empty field).
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <param name="field">When this method returns <see langword="true"/>, the parsed field.</param>
    /// <param name="error">When this method returns <see langword="false"/>, the reason parsing failed.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> input, out HttpDigestField field, out string? error)
    {
        field = default;

        if (!StructuredFieldDictionary.TryParse(input, out StructuredFieldDictionary dictionary, out error))
        {
            // Malformed structured-field syntax: bad base64, missing colons, unbalanced quotes, etc.
            return false;
        }

        if (dictionary.Count == 0)
        {
            error = "A digest field must carry at least one algorithm entry.";
            return false;
        }

        var entries = new List<HttpDigestEntry>(dictionary.Count);
        foreach (KeyValuePair<string, StructuredFieldMember> member in dictionary)
        {
            StructuredFieldMember value = member.Value;
            if (value.IsInnerList || value.Item.Value.Type != StructuredFieldType.ByteSequence)
            {
                error = $"The digest field member '{member.Key}' must be a Byte Sequence (RFC 9530 §2/§3).";
                return false;
            }

            if (HttpDigestAlgorithm.TryParse(member.Key, out HttpDigestAlgorithm algorithm))
            {
                entries.Add(new HttpDigestEntry(algorithm, value.Item.Value.AsByteSequence()));
            }
            // An unregistered but well-formed key is preserved in `dictionary` for round-tripping
            // and deliberately not surfaced as a typed entry (RFC 9530: ignore unknown algorithms).
        }

        field = new HttpDigestField(dictionary, entries.ToArray());
        return true;
    }

    /// <summary>
    /// Parses a <c>Content-Digest</c> / <c>Repr-Digest</c> field value, throwing on failure.
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <returns>The parsed field.</returns>
    /// <exception cref="HttpDigestException">The value is not a well-formed digest field.</exception>
    public static HttpDigestField Parse(ReadOnlySpan<char> input)
    {
        if (!TryParse(input, out HttpDigestField field, out string? error))
        {
            throw new HttpDigestException(error ?? "Malformed digest field.");
        }
        return field;
    }

    #endregion

    #region Compute

    /// <summary>
    /// Computes a digest field over <paramref name="content"/> for a single algorithm.
    /// </summary>
    /// <param name="content">The content to hash (the message content for <c>Content-Digest</c>).</param>
    /// <param name="algorithm">The algorithm to compute with; must be supported.</param>
    /// <returns>A field carrying the single computed digest.</returns>
    /// <exception cref="ArgumentException"><paramref name="algorithm"/> is not supported for computation.</exception>
    public static HttpDigestField ForContent(ReadOnlySpan<byte> content, HttpDigestAlgorithm algorithm)
    {
        if (!algorithm.IsSupported)
        {
            throw new ArgumentException(
                $"The digest algorithm '{algorithm}' cannot be used to generate a digest (RFC 9530 §5).", nameof(algorithm));
        }

        var entry = new HttpDigestEntry(algorithm, ComputeHash(algorithm, content));
        return FromEntries(new[] { entry });
    }

    /// <summary>
    /// Computes a digest field over <paramref name="content"/> for one or more algorithms, in the
    /// given order.
    /// </summary>
    /// <param name="content">The content to hash.</param>
    /// <param name="algorithms">The algorithms to compute with; each must be supported.</param>
    /// <returns>A field carrying a digest per algorithm.</returns>
    /// <exception cref="ArgumentException"><paramref name="algorithms"/> is empty or contains an unsupported algorithm.</exception>
    public static HttpDigestField ForContent(ReadOnlySpan<byte> content, params HttpDigestAlgorithm[] algorithms)
    {
        ArgumentNullException.ThrowIfNull(algorithms);
        if (algorithms.Length == 0)
        {
            throw new ArgumentException("At least one algorithm is required.", nameof(algorithms));
        }

        var entries = new HttpDigestEntry[algorithms.Length];
        for (int i = 0; i < algorithms.Length; i++)
        {
            HttpDigestAlgorithm algorithm = algorithms[i];
            if (!algorithm.IsSupported)
            {
                throw new ArgumentException(
                    $"The digest algorithm '{algorithm}' cannot be used to generate a digest (RFC 9530 §5).", nameof(algorithms));
            }
            entries[i] = new HttpDigestEntry(algorithm, ComputeHash(algorithm, content));
        }

        return FromEntries(entries);
    }

    /// <summary>
    /// Computes a digest field over a stream for one or more algorithms, reading the stream once
    /// and feeding every algorithm's incremental hash in parallel.
    /// </summary>
    /// <param name="content">The forward-only content stream to hash.</param>
    /// <param name="algorithms">The algorithms to compute with; each must be supported.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>A field carrying a digest per algorithm.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="algorithms"/> is empty or contains an unsupported algorithm.</exception>
    public static async ValueTask<HttpDigestField> ForContentAsync(
        Stream content,
        HttpDigestAlgorithm[] algorithms,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(algorithms);
        if (algorithms.Length == 0)
        {
            throw new ArgumentException("At least one algorithm is required.", nameof(algorithms));
        }

        using var digester = new HttpContentDigester(algorithms);
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            digester.Append(buffer.AsSpan(0, read));
        }
        return digester.ToField();
    }

    internal static HttpDigestField FromEntries(HttpDigestEntry[] entries)
    {
        var members = new KeyValuePair<string, StructuredFieldMember>[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            HttpDigestEntry entry = entries[i];
            StructuredFieldBareItem bytes = StructuredFieldBareItem.FromByteSequence(entry.Digest.Span);
            members[i] = new KeyValuePair<string, StructuredFieldMember>(
                entry.Algorithm.Key!,
                StructuredFieldMember.FromItem(new StructuredFieldItem(bytes)));
        }
        return new HttpDigestField(new StructuredFieldDictionary(members), entries);
    }

    internal static byte[] ComputeHash(HttpDigestAlgorithm algorithm, ReadOnlySpan<byte> content)
    {
        using IncrementalHash hash = algorithm.CreateIncrementalHash();
        hash.AppendData(content);
        return hash.GetHashAndReset();
    }

    #endregion

    #region Verify

    /// <summary>
    /// Verifies <paramref name="content"/> against every supported digest the field carries.
    /// </summary>
    /// <param name="content">The content to hash and compare.</param>
    /// <returns>
    /// <see cref="HttpDigestVerificationResult.Matched"/> if all supported digests match,
    /// <see cref="HttpDigestVerificationResult.Mismatched"/> if any supported digest differs, or
    /// <see cref="HttpDigestVerificationResult.NoSupportedAlgorithm"/> if the field carried nothing
    /// this library can verify with.
    /// </returns>
    public HttpDigestVerificationResult VerifyContent(ReadOnlySpan<byte> content)
    {
        if (_entries is null)
        {
            return HttpDigestVerificationResult.NoSupportedAlgorithm;
        }

        bool anySupported = false;
        Span<byte> computed = stackalloc byte[64]; // large enough for SHA-512
        foreach (HttpDigestEntry entry in _entries)
        {
            if (!entry.Algorithm.IsSupported)
            {
                continue;
            }
            anySupported = true;

            using IncrementalHash hash = entry.Algorithm.CreateIncrementalHash();
            hash.AppendData(content);
            int written = hash.GetHashAndReset(computed);

            // FixedTimeEquals is length-checked, so a truncated or oversized digest value is a
            // mismatch rather than an exception.
            if (!CryptographicOperations.FixedTimeEquals(computed[..written], entry.Digest.Span))
            {
                return HttpDigestVerificationResult.Mismatched;
            }
        }

        return anySupported
            ? HttpDigestVerificationResult.Matched
            : HttpDigestVerificationResult.NoSupportedAlgorithm;
    }

    #endregion

    /// <summary>
    /// Serializes the field to its RFC 9651 &#167; 4.1.2 canonical dictionary form
    /// (<c>algorithm=:base64:</c>, comma-separated). Round-trips every member preserved on parse,
    /// including deprecated and unregistered algorithms.
    /// </summary>
    /// <returns>The canonical field value, or the empty string for the default instance.</returns>
    public string Serialize() => _dictionary.Serialize();

    /// <inheritdoc />
    public override string ToString() => Serialize();
}
