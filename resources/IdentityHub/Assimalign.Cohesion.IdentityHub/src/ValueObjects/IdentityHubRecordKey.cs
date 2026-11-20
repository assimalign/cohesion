using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The key of a record within its record set, and the value a record lists under an index.
/// </summary>
/// <remarks>
/// <para>
/// A key is 1 to 512 characters with no control character and no unpaired surrogate, so that
/// every distinct key has a distinct UTF-8 form. Keys compare ordinally: they are case-sensitive
/// and are never normalized. A caller that wants two spellings to be one key, such as a user
/// name in two cases, normalizes before it forms the key. A store on a medium that compares
/// differently, such as a case-insensitive collation, encodes the key to keep the ordinal rule.
/// </para>
/// <para>
/// <b>A key is never a secret.</b> Stores keep keys and index values in the clear, in indexes
/// and in diagnostics. A bearer value such as a device code or a refresh token is hashed before
/// it is used as a key.
/// </para>
/// <para>
/// <see langword="default"/> holds no key and every store operation refuses it.
/// </para>
/// </remarks>
public readonly partial struct IdentityHubRecordKey
{
    /// <summary>
    /// The longest key, in characters.
    /// </summary>
    public const int MaxLength = IdentityHubGrammar.MaxKeyLength;

    /// <summary>
    /// Determines whether a value is a record key.
    /// </summary>
    /// <param name="value">The candidate key.</param>
    /// <param name="message">The reason the key is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value follows the record key grammar.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // The value is not echoed: it may hold the control character that made it invalid.
        if (!IdentityHubGrammar.IsKey(value))
        {
            message = ErrorMessages.RecordKeyInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
