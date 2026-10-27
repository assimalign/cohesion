namespace Assimalign.Cohesion.Database.Sql;

/// <summary>A single SQL schema or object name, compared ordinally ignoring case.</summary>
/// <remarks>
/// Supply the decoded identifier, without SQL delimiters. Delimited identifiers may contain
/// spaces and punctuation; like catalog names, they must contain a non-whitespace character.
/// This is one name component, so a dot is part of the name rather than a qualifier.
/// </remarks>
public partial struct SqlObjectName
{
    /// <summary>Checks whether a decoded name can identify a catalog object.</summary>
    /// <param name="value">The decoded name.</param>
    /// <param name="message">The refusal detail, or null for a valid name.</param>
    /// <returns>Whether the name contains a non-whitespace character.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            message = ErrorMessages.SqlObjectNameInvalid;
            return false;
        }
        message = null;
        return true;
    }
}
