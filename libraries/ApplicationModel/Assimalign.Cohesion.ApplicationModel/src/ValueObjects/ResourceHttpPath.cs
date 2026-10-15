using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// An absolute resource HTTP probe path or control-plane prefix, without a query or fragment.
/// </summary>
/// <remarks>
/// Paths retain their case and whitespace and compare ordinally, following RFC 3986 section
/// 6.2.2.1. A default value has no path and must be refused where a path is required.
/// </remarks>
public partial struct ResourceHttpPath
{
    /// <summary>Determines whether a value is an absolute path without a query or fragment.</summary>
    /// <param name="value">The candidate path.</param>
    /// <param name="message">The refusal reason, or null when the path is valid.</param>
    /// <returns>Whether the value begins with '/' and has no query or fragment delimiter.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] != '/' ||
            value.IndexOfAny(['?', '#']) >= 0)
        {
            message = ErrorMessages.ResourceHttpPathInvalid;
            return false;
        }

        message = null;
        return true;
    }

    /// <summary>Converts an optional path, preserving null as an absent path.</summary>
    /// <param name="value">The path, or null for omission.</param>
    /// <returns>The checked path, or null.</returns>
    /// <exception cref="ArgumentException">The path is invalid.</exception>
    public static implicit operator ResourceHttpPath?(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return new ResourceHttpPath(value);
    }
}
