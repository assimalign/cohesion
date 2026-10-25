using System;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Identifies an action a resource exposes, such as <c>flush-cache</c>: lowercase ASCII letters and digits
/// in hyphen-separated words, at most 64 characters.
/// </summary>
public readonly partial struct ResourceActionName
{
    /// <summary>Validates the action name grammar.</summary>
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

        message = ErrorMessages.ResourceActionNameInvalid;
        return false;
    }
}
