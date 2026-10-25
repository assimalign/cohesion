namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Identifies a logical resource group within an application.</summary>
/// <remarks>Names are case-sensitive ASCII path segments of one to 128 letters, digits, periods, underscores, or hyphens.</remarks>
public partial struct ResourceGroupName
{
    /// <summary>Checks a logical group name without applying infrastructure naming rules.</summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The rejection reason, or null when valid.</param>
    /// <returns>Whether the name is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value is "." or "..")
        {
            message = ErrorMessages.ResourceGroupNameInvalid;
            return false;
        }
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                message = ErrorMessages.ResourceGroupNameInvalid;
                return false;
            }
        }
        message = null;
        return true;
    }
}
