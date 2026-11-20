using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The name of a record set in an <see cref="IIdentityHubStore"/>: the collection a record's
/// key is unique within.
/// </summary>
/// <remarks>
/// A name is 1 to 64 characters of lowercase ASCII letters and digits, in segments separated
/// by one <c>.</c> or <c>-</c>, starting with a letter: <c>signing-keys</c>,
/// <c>directory.users</c>. The grammar is narrow so that a store can use the name as a table,
/// container, or key prefix without escaping it. Names compare ordinally.
/// <see langword="default"/> holds no name and every store operation refuses it.
/// </remarks>
public readonly partial struct IdentityHubRecordSetName
{
    /// <summary>
    /// The longest record set name, in characters.
    /// </summary>
    public const int MaxLength = IdentityHubGrammar.MaxNameLength;

    /// <summary>
    /// Determines whether a value is a record set name.
    /// </summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The reason the name is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value follows the record set name grammar.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // The value is not echoed: it may hold the character that made it invalid.
        if (!IdentityHubGrammar.IsName(value))
        {
            message = ErrorMessages.RecordSetNameInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
