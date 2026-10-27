namespace Assimalign.Cohesion.Database.KeyValuePair.Protocol;

/// <summary>A case-insensitive key-value scalar function identifier.</summary>
/// <remarks>Names contain ASCII letters, digits and underscores, and start with a letter or underscore.</remarks>
public readonly partial struct KeyValueFunctionName
{
    /// <summary>Checks the shared CALL identifier grammar.</summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="message">The resource-backed refusal reason, or null when valid.</param>
    /// <returns>Whether the candidate is a valid function identifier.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrEmpty(value) || !(char.IsAsciiLetter(value[0]) || value[0] == '_'))
        {
            message = ErrorMessages.FunctionNameInvalid;
            return false;
        }
        foreach (char character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                message = ErrorMessages.FunctionNameInvalid;
                return false;
            }
        }
        message = null;
        return true;
    }
}
