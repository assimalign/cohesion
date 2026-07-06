namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// Creates purpose-bound <see cref="IDataProtector"/> instances. A protector derived for
/// one purpose can neither read nor forge payloads produced for a different purpose, so a
/// single application key ring safely backs many independent protection scopes
/// (antiforgery tokens, auth cookies, session identifiers, and so on).
/// </summary>
/// <remarks>
/// The provider is the composition-root entry point: it is created once (typically at
/// builder time in a <c>*.Hosting</c> project) over a configured key ring, then shared.
/// Consumers ask it for a protector scoped to their purpose; they never see key material.
/// </remarks>
public interface IDataProtectionProvider
{
    /// <summary>
    /// Creates a protector scoped to <paramref name="purpose"/>. The purpose is folded into
    /// the subkey derivation, so protectors created for different purposes are
    /// cryptographically isolated from one another.
    /// </summary>
    /// <param name="purpose">
    /// A stable, application-defined discriminator for the protection scope (for example
    /// <c>"Cohesion.Http.Antiforgery.v1"</c>). The value must be identical on every node and
    /// across restarts for payloads to remain readable; it is not a secret.
    /// </param>
    /// <returns>A protector bound to <paramref name="purpose"/>.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="purpose"/> is <see langword="null"/>.</exception>
    IDataProtector CreateProtector(string purpose);
}
