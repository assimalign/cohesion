using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using Assimalign.Cohesion.IdentityHub.Internal;
using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// A client secret of an app registration: a shared secret the application presents to prove
/// its identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>The secret itself is never kept.</b> The credential holds a verifier derived from the
/// secret with PBKDF2-HMAC-SHA256 and a salt of its own, and the number of iterations that
/// derived it. That is all a store ever receives, and <see cref="Verify"/> is the only thing
/// the verifier is good for.
/// </para>
/// <para>
/// <b>Prefer <see cref="Generate"/>.</b> It makes a secret of 256 random bits and hands it back
/// once, as a hub that issues secrets does. Such a secret cannot be guessed, so its verifier is
/// derived in one iteration and verifying it is cheap. <see cref="Create"/> takes a secret the
/// caller chose, which may be guessable, so its verifier is derived in
/// <see cref="DerivationIterations"/> iterations: a stolen verifier is expensive to search, and
/// each verification costs the hub the same work. An endpoint that verifies such secrets for
/// callers it has not authenticated limits how often it does so.
/// </para>
/// <para>
/// A secret is 1 to 512 characters of printable ASCII, the VSCHAR string of RFC 6749
/// appendix A.2, and is not blank.
/// </para>
/// </remarks>
[DebuggerDisplay("{Id,nq} (secret)")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class IdentityClientSecretCredential : IdentityClientCredential
{
    /// <summary>
    /// The longest secret, in characters.
    /// </summary>
    public const int MaxSecretLength = IdentityClientGrammar.MaxSecretLength;

    /// <summary>
    /// The number of PBKDF2-HMAC-SHA256 iterations that derive the verifier of a secret the
    /// caller chose.
    /// </summary>
    public const int DerivationIterations = 600_000;

    private const int saltLength = 16;
    private const int maxSaltLength = 64;
    private const int verifierLength = 32;
    private const int generatedLength = 32;

    // A stored registration is read back through Restore. The cap keeps a damaged or forged
    // record from turning one verification into minutes of work.
    private const int maxIterations = 10_000_000;

    private readonly byte[] _salt;
    private readonly byte[] _verifier;
    private readonly int _iterations;

    private IdentityClientSecretCredential(
        IdentityClientCredentialId id,
        string? displayName,
        DateTimeOffset? notBefore,
        DateTimeOffset? expiresAt,
        byte[] salt,
        byte[] verifier,
        int iterations)
        : base(id, displayName, notBefore, expiresAt)
    {
        _salt = salt;
        _verifier = verifier;
        _iterations = iterations;
    }

    /// <inheritdoc />
    public override IdentityCredentialKind Kind => IdentityCredentialKind.Key;

    internal ReadOnlySpan<byte> Salt => _salt;

    internal ReadOnlySpan<byte> Verifier => _verifier;

    internal int Iterations => _iterations;

    /// <summary>
    /// Creates a credential from a secret the caller chose.
    /// </summary>
    /// <remarks>
    /// The call derives the verifier in <see cref="DerivationIterations"/> iterations, which
    /// takes a noticeable fraction of a second, and keeps no copy of <paramref name="secret"/>.
    /// </remarks>
    /// <param name="id">The credential's identifier.</param>
    /// <param name="secret">The secret: 1 to 512 characters of printable ASCII, not blank.</param>
    /// <param name="displayName">The name a person is shown for the credential, or <see langword="null"/> for none.</param>
    /// <param name="notBefore">The instant from which the secret is accepted, or <see langword="null"/> for from the start.</param>
    /// <param name="expiresAt">The instant from which the secret is no longer accepted, or <see langword="null"/> for never.</param>
    /// <returns>The credential.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="secret"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is <see langword="default"/>; <paramref name="secret"/> or
    /// <paramref name="displayName"/> breaks its rule; or <paramref name="expiresAt"/> is not after
    /// <paramref name="notBefore"/>. The secret is never part of the message.
    /// </exception>
    public static IdentityClientSecretCredential Create(
        IdentityClientCredentialId id,
        string secret,
        string? displayName = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!IdentityClientGrammar.IsSecret(secret))
        {
            throw new ArgumentException(ErrorMessages.CredentialSecretInvalid, nameof(secret));
        }

        return Derive(id, secret, DerivationIterations, displayName, notBefore, expiresAt);
    }

    /// <summary>
    /// Creates a credential with a new random secret and hands the secret back once.
    /// </summary>
    /// <remarks>
    /// The secret is 256 random bits, written as 43 characters of base64url. It is not kept:
    /// the caller shows or delivers it now, and it cannot be read again.
    /// </remarks>
    /// <param name="id">The credential's identifier.</param>
    /// <param name="secret">The secret the application presents.</param>
    /// <param name="displayName">The name a person is shown for the credential, or <see langword="null"/> for none.</param>
    /// <param name="notBefore">The instant from which the secret is accepted, or <see langword="null"/> for from the start.</param>
    /// <param name="expiresAt">The instant from which the secret is no longer accepted, or <see langword="null"/> for never.</param>
    /// <returns>The credential.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is <see langword="default"/>; <paramref name="displayName"/> breaks its
    /// rule; or <paramref name="expiresAt"/> is not after <paramref name="notBefore"/>.
    /// </exception>
    public static IdentityClientSecretCredential Generate(
        IdentityClientCredentialId id,
        out string secret,
        string? displayName = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expiresAt = null)
    {
        Span<byte> random = stackalloc byte[generatedLength];
        RandomNumberGenerator.Fill(random);
        try
        {
            string generated = Base64Url.EncodeToString(random);

            // A secret of 256 random bits gains nothing from more iterations.
            IdentityClientSecretCredential credential = Derive(id, generated, 1, displayName, notBefore, expiresAt);
            secret = generated;
            return credential;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
        }
    }

    /// <summary>
    /// Determines whether a presented secret is this credential's secret and the credential is
    /// accepted at an instant.
    /// </summary>
    /// <remarks>
    /// The comparison takes the same time whether the secret is wrong in its first character
    /// or its last. A secret outside the grammar, and any secret presented outside the
    /// credential's validity window, is refused without deriving anything.
    /// </remarks>
    /// <param name="secret">The secret the application presented.</param>
    /// <param name="at">The instant the secret was presented at.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="secret"/> is the credential's secret and
    /// <see cref="IdentityClientCredential.IsUsable"/> is <see langword="true"/> at <paramref name="at"/>.
    /// </returns>
    public bool Verify(string? secret, DateTimeOffset at)
    {
        if (!IsUsable(at) || !IdentityClientGrammar.IsSecret(secret))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[MaxSecretLength];
        Span<byte> candidate = stackalloc byte[verifierLength];
        int count = Encoding.ASCII.GetBytes(secret, bytes);
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(bytes[..count], _salt, candidate, _iterations, HashAlgorithmName.SHA256);
            return CryptographicOperations.FixedTimeEquals(candidate, _verifier);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    /// <summary>
    /// Rebuilds a credential from the verifier a codec stored.
    /// </summary>
    /// <exception cref="FormatException">The stored verifier is not one this type derives.</exception>
    internal static IdentityClientSecretCredential Restore(
        IdentityClientCredentialId id,
        string? displayName,
        DateTimeOffset? notBefore,
        DateTimeOffset? expiresAt,
        byte[]? salt,
        byte[]? verifier,
        int iterations)
    {
        if (salt is null || salt.Length is < saltLength or > maxSaltLength ||
            verifier is null || verifier.Length is not verifierLength ||
            iterations is < 1 or > maxIterations)
        {
            throw new FormatException(ErrorMessages.CredentialVerifierInvalid);
        }

        return new IdentityClientSecretCredential(
            id,
            displayName,
            notBefore,
            expiresAt,
            (byte[])salt.Clone(),
            (byte[])verifier.Clone(),
            iterations);
    }

    private static IdentityClientSecretCredential Derive(
        IdentityClientCredentialId id,
        string secret,
        int iterations,
        string? displayName,
        DateTimeOffset? notBefore,
        DateTimeOffset? expiresAt)
    {
        // The base constructor validates the identifier, the display name, and the window, so
        // it runs before the derivation and an invalid argument costs nothing.
        byte[] salt = new byte[saltLength];
        byte[] verifier = new byte[verifierLength];
        var credential = new IdentityClientSecretCredential(id, displayName, notBefore, expiresAt, salt, verifier, iterations);

        RandomNumberGenerator.Fill(salt);
        Span<byte> bytes = stackalloc byte[MaxSecretLength];
        int count = Encoding.ASCII.GetBytes(secret, bytes);
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(bytes[..count], salt, verifier, iterations, HashAlgorithmName.SHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        return credential;
    }

    private sealed class DebugView
    {
        private readonly IdentityClientSecretCredential _credential;

        public DebugView(IdentityClientSecretCredential credential)
        {
            _credential = credential;
        }

        public IdentityClientCredentialId Id => _credential.Id;

        public IdentityCredentialKind Kind => _credential.Kind;

        public string? DisplayName => _credential.DisplayName;

        public DateTimeOffset? NotBefore => _credential.NotBefore;

        public DateTimeOffset? ExpiresAt => _credential.ExpiresAt;
    }
}
