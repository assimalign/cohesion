using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Identifies a state contract using one to 128 ASCII letters, digits, dots, hyphens or underscores.</summary>
public readonly partial struct ResourceStateName
{
    /// <summary>Validates the state name grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value is "." or "..")
        {
            message = ErrorMessages.ResourceStateNameInvalid;
            return false;
        }
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                message = ErrorMessages.ResourceStateNameInvalid;
                return false;
            }
        }
        message = null;
        return true;
    }
}
