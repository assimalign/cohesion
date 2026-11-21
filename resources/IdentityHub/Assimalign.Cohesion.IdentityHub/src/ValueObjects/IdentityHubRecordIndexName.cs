using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The name of an index within a record set: a second way to find the set's records, beside
/// their keys.
/// </summary>
/// <remarks>
/// An index name follows the same grammar as <see cref="IdentityHubRecordSetName"/>: 1 to 64
/// characters of lowercase ASCII letters and digits, in segments separated by one <c>.</c> or
/// <c>-</c>, starting with a letter (<c>user-code</c>, <c>subject</c>). Names compare
/// ordinally. <see langword="default"/> holds no name and is refused wherever an index is named.
/// </remarks>
public readonly partial struct IdentityHubRecordIndexName
{
    /// <summary>
    /// The longest index name, in characters.
    /// </summary>
    public const int MaxLength = IdentityHubGrammar.MaxNameLength;

    /// <summary>
    /// Determines whether a value is an index name.
    /// </summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The reason the name is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value follows the index name grammar.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // The value is not echoed: it may hold the character that made it invalid.
        if (!IdentityHubGrammar.IsName(value))
        {
            message = ErrorMessages.RecordIndexNameInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
