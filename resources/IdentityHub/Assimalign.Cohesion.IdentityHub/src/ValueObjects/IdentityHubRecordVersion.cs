using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The version a store assigned to one stored state of a record: the token a conditional
/// replace or remove names to act only on the state its caller read.
/// </summary>
/// <remarks>
/// <para>
/// A version is opaque to everything but the store that issued it. Callers compare versions
/// for equality only and never order, parse, or compute them. The store issues a new version
/// on every successful write, and never issues the same version twice for one key in one
/// record set, so a version read before a record was removed and added again never matches
/// the new record.
/// </para>
/// <para>
/// A version is 1 to 128 visible ASCII characters with no space. A store maps its own form
/// to that: a counter, a row version, an entity tag. <see langword="default"/> holds no
/// version and every conditional operation refuses it.
/// </para>
/// </remarks>
public readonly partial struct IdentityHubRecordVersion
{
    /// <summary>
    /// The longest version, in characters.
    /// </summary>
    public const int MaxLength = IdentityHubGrammar.MaxVersionLength;

    /// <summary>
    /// Determines whether a value is a record version.
    /// </summary>
    /// <param name="value">The candidate version.</param>
    /// <param name="message">The reason the version is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value follows the record version grammar.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (!IdentityHubGrammar.IsVersion(value))
        {
            message = ErrorMessages.RecordVersionInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
