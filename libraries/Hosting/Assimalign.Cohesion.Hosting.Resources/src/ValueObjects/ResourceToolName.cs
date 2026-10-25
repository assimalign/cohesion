using System;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Identifies an operator tool a resource offers, such as <c>sql</c>: lowercase ASCII letters and digits
/// in hyphen-separated words, at most 64 characters.
/// </summary>
public readonly partial struct ResourceToolName
{
    /// <summary>Validates the tool name grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (ResourceSurfaceNameGrammar.IsValid(value))
        {
            message = null;
            return true;
        }

        message = ErrorMessages.ResourceToolNameInvalid;
        return false;
    }
}
