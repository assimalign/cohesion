using System;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// An algorithm from the RFC 9530 &#167; 5.2 "Hash Algorithms for HTTP Digest Fields" registry,
/// identified by its lowercase registry key (for example <c>sha-256</c>). The type distinguishes
/// the two <em>active</em>, cryptographically sound algorithms this library computes and verifies
/// with — <see cref="Sha256"/> and <see cref="Sha512"/> — from the <em>deprecated/insecure</em>
/// registry entries (<see cref="Md5"/>, <see cref="Sha"/>, <see cref="UnixSum"/>,
/// <see cref="UnixCksum"/>) which are recognized on parse but, per RFC 9530 &#167; 5, are never
/// used to generate or validate a digest.
/// </summary>
/// <remarks>
/// <para>
/// A digest field carries a dictionary keyed by these algorithm tokens. Recognizing an insecure
/// entry (rather than rejecting it) lets a recipient enumerate every offered algorithm and pick a
/// supported one, while <see cref="IsSupported"/> gates whether the entry may participate in
/// computation or verification.
/// </para>
/// <para>
/// The default value of this struct is the unregistered, unsupported algorithm
/// (<see cref="IsRegistered"/> and <see cref="IsSupported"/> are both <see langword="false"/>);
/// it models a token that is syntactically a valid key but absent from the registry.
/// </para>
/// </remarks>
[DebuggerDisplay("{Key,nq} (Supported={IsSupported})")]
public readonly struct HttpDigestAlgorithm : IEquatable<HttpDigestAlgorithm>
{
    private readonly string? _key;
    private readonly HashAlgorithmName _hashName;
    private readonly int _hashLengthInBytes;
    private readonly bool _supported;

    private HttpDigestAlgorithm(string key, bool supported, HashAlgorithmName hashName, int hashLengthInBytes)
    {
        _key = key;
        _supported = supported;
        _hashName = hashName;
        _hashLengthInBytes = hashLengthInBytes;
    }

    /// <summary>The <c>sha-256</c> algorithm (RFC 9530 &#167; 5.2) — active, supported.</summary>
    public static HttpDigestAlgorithm Sha256 { get; } = new("sha-256", supported: true, HashAlgorithmName.SHA256, 32);

    /// <summary>The <c>sha-512</c> algorithm (RFC 9530 &#167; 5.2) — active, supported.</summary>
    public static HttpDigestAlgorithm Sha512 { get; } = new("sha-512", supported: true, HashAlgorithmName.SHA512, 64);

    /// <summary>The <c>md5</c> algorithm (RFC 9530 &#167; 5.2) — deprecated, recognized but never used.</summary>
    public static HttpDigestAlgorithm Md5 { get; } = new("md5", supported: false, default, 0);

    /// <summary>The <c>sha</c> (SHA-1) algorithm (RFC 9530 &#167; 5.2) — deprecated, recognized but never used.</summary>
    public static HttpDigestAlgorithm Sha { get; } = new("sha", supported: false, default, 0);

    /// <summary>The <c>unixsum</c> algorithm (RFC 9530 &#167; 5.2) — deprecated, recognized but never used.</summary>
    public static HttpDigestAlgorithm UnixSum { get; } = new("unixsum", supported: false, default, 0);

    /// <summary>The <c>unixcksum</c> algorithm (RFC 9530 &#167; 5.2) — deprecated, recognized but never used.</summary>
    public static HttpDigestAlgorithm UnixCksum { get; } = new("unixcksum", supported: false, default, 0);

    /// <summary>
    /// Gets the lowercase RFC 9530 registry key for this algorithm (for example <c>sha-256</c>),
    /// or <see langword="null"/> for the default (unregistered) value.
    /// </summary>
    public string? Key => _key;

    /// <summary>
    /// Gets a value indicating whether this algorithm is a recognized RFC 9530 registry entry —
    /// <see langword="true"/> for both the active algorithms and the deprecated ones.
    /// </summary>
    public bool IsRegistered => _key is not null;

    /// <summary>
    /// Gets a value indicating whether this library will compute and verify digests with this
    /// algorithm. Only the active, cryptographically sound algorithms (<see cref="Sha256"/>,
    /// <see cref="Sha512"/>) are supported; deprecated entries return <see langword="false"/>.
    /// </summary>
    public bool IsSupported => _supported;

    /// <summary>
    /// Gets the length, in bytes, of a digest produced by this algorithm (32 for
    /// <see cref="Sha256"/>, 64 for <see cref="Sha512"/>). Zero for unsupported algorithms.
    /// </summary>
    public int HashLengthInBytes => _hashLengthInBytes;

    /// <summary>
    /// Attempts to resolve a registry key (case-insensitive) to a recognized
    /// <see cref="HttpDigestAlgorithm"/>, including the deprecated entries.
    /// </summary>
    /// <param name="key">The algorithm key, for example <c>sha-256</c>.</param>
    /// <param name="algorithm">
    /// When this method returns <see langword="true"/>, the recognized algorithm; otherwise the
    /// default value.
    /// </param>
    /// <returns><see langword="true"/> if <paramref name="key"/> is a registered algorithm; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? key, out HttpDigestAlgorithm algorithm)
    {
        // RFC 9530 keys are lowercase tokens; match case-insensitively and canonicalize to the
        // well-known instance so callers always observe the registry spelling.
        if (key is not null)
        {
            if (string.Equals(key, "sha-256", StringComparison.OrdinalIgnoreCase)) { algorithm = Sha256; return true; }
            if (string.Equals(key, "sha-512", StringComparison.OrdinalIgnoreCase)) { algorithm = Sha512; return true; }
            if (string.Equals(key, "md5", StringComparison.OrdinalIgnoreCase)) { algorithm = Md5; return true; }
            if (string.Equals(key, "sha", StringComparison.OrdinalIgnoreCase)) { algorithm = Sha; return true; }
            if (string.Equals(key, "unixsum", StringComparison.OrdinalIgnoreCase)) { algorithm = UnixSum; return true; }
            if (string.Equals(key, "unixcksum", StringComparison.OrdinalIgnoreCase)) { algorithm = UnixCksum; return true; }
        }

        algorithm = default;
        return false;
    }

    /// <summary>
    /// Creates a BCL <see cref="IncrementalHash"/> for this algorithm. Used by the computation and
    /// verification paths so hashing is always incremental and AOT-safe (no reflection, no runtime
    /// codegen).
    /// </summary>
    /// <returns>An incremental hash for this algorithm; the caller owns and disposes it.</returns>
    /// <exception cref="NotSupportedException">This algorithm is not supported for hashing (<see cref="IsSupported"/> is <see langword="false"/>).</exception>
    internal IncrementalHash CreateIncrementalHash()
    {
        if (!_supported)
        {
            throw new NotSupportedException(
                $"The digest algorithm '{_key ?? "(unregistered)"}' is not supported for computation or validation (RFC 9530 §5).");
        }

        return IncrementalHash.CreateHash(_hashName);
    }

    /// <inheritdoc />
    public bool Equals(HttpDigestAlgorithm other) => string.Equals(_key, other._key, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpDigestAlgorithm other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _key is null ? 0 : StringComparer.Ordinal.GetHashCode(_key);

    /// <summary>Returns the algorithm's registry key, or <c>"(unregistered)"</c> for the default value.</summary>
    /// <returns>The registry key.</returns>
    public override string ToString() => _key ?? "(unregistered)";

    /// <summary>Determines whether two algorithms are the same registry entry.</summary>
    /// <param name="left">The first algorithm.</param>
    /// <param name="right">The second algorithm.</param>
    /// <returns><see langword="true"/> if the algorithms are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(HttpDigestAlgorithm left, HttpDigestAlgorithm right) => left.Equals(right);

    /// <summary>Determines whether two algorithms are different registry entries.</summary>
    /// <param name="left">The first algorithm.</param>
    /// <param name="right">The second algorithm.</param>
    /// <returns><see langword="true"/> if the algorithms are unequal; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(HttpDigestAlgorithm left, HttpDigestAlgorithm right) => !left.Equals(right);
}
