namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>An unquoted GQL function identifier, compared ordinally ignoring case.</summary>
/// <remarks>Default is refused at every function registration boundary.</remarks>
public partial struct GraphFunctionName
{
    /// <summary>Checks the lexer identifier grammar.</summary>
    /// <param name="value">The candidate name.</param><param name="message">The refusal reason, or null.</param>
    /// <returns>Whether the name is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrEmpty(value) || !(char.IsLetter(value[0]) || value[0] == '_'))
        { message = ErrorMessages.FunctionNameInvalid; return false; }
        foreach (char character in value)
        {
            if (!(char.IsLetterOrDigit(character) || character == '_'))
            { message = ErrorMessages.FunctionNameInvalid; return false; }
        }
        message = null; return true;
    }
}
