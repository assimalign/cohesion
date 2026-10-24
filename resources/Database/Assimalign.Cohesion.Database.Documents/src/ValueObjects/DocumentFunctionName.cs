namespace Assimalign.Cohesion.Database.Documents;

/// <summary>An unquoted OQL function identifier, compared ordinally ignoring case.</summary>
/// <remarks>Matches TokenLexer identifiers: a letter or underscore followed by letters, digits or underscores. Default is refused by function boundaries.</remarks>
public partial struct DocumentFunctionName
{
    /// <summary>Checks the unquoted identifier grammar.</summary><param name="value">The candidate.</param><param name="message">The failure detail, or null.</param><returns>Whether the name is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrEmpty(value) || !(char.IsLetter(value[0]) || value[0] == '_'))
        {
            message = ErrorMessages.FunctionNameInvalid;
            return false;
        }
        foreach (char character in value)
        {
            if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                message = ErrorMessages.FunctionNameInvalid;
                return false;
            }
        }
        message = null;
        return true;
    }
}
