using System;
using System.Diagnostics.CodeAnalysis;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.Web.Antiforgery.Internal;

/// <summary>
/// Adapts a purpose-bound data-protection <see cref="IDataProtector"/> to the antiforgery engine's
/// <see cref="IHttpAntiforgeryProtector"/> seam, so tokens are sealed with the application's rotating key
/// ring instead of a per-process random key.
/// </summary>
/// <remarks>
/// <para>
/// The key ring persists its keys and names the producing key in every payload, so tokens minted before
/// a restart, or by another instance sharing the key repository, still validate. The protector is bound
/// to the antiforgery purpose chain, so neither an authentication ticket nor any other payload the same
/// ring protects can stand in for a token.
/// </para>
/// <para>
/// <see cref="TryUnprotect(ReadOnlySpan{byte}, out byte[])"/> is fed untrusted request input. Every
/// verification and key-lifecycle failure surfaces from the ring as <see cref="DataProtectionException"/>,
/// which maps to "invalid" here. Anything else, such as an unreadable key repository, is an
/// infrastructure fault and propagates.
/// </para>
/// </remarks>
internal sealed class DataProtectionAntiforgeryProtector : IHttpAntiforgeryProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionAntiforgeryProtector(IDataProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);

        _protector = protector;
    }

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext) => _protector.Protect(plaintext);

    /// <inheritdoc />
    public bool TryUnprotect(ReadOnlySpan<byte> protectedData, [NotNullWhen(true)] out byte[]? plaintext)
    {
        try
        {
            plaintext = _protector.Unprotect(protectedData);
            return true;
        }
        catch (DataProtectionException)
        {
            // A forged, truncated, foreign-purpose, or aged-out payload is a validation failure, not an
            // exceptional condition for the caller.
            plaintext = null;
            return false;
        }
    }
}
