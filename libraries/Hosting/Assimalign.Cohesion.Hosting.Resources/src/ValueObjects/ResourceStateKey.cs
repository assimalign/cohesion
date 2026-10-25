using System;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Identifies a state slot as one to 256 non-control characters; whitespace alone is invalid.</summary>
public readonly partial struct ResourceStateKey
{
    /// <summary>Validates a state key before a public dispatch boundary.</summary>
    /// <param name="value">The candidate key.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the key is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value is "." or "..")
        {
            message = ErrorMessages.ResourceStateKeyInvalid;
            return false;
        }
        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                message = ErrorMessages.ResourceStateKeyInvalid;
                return false;
            }
        }
        message = null;
        return true;
    }
}
