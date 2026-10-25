using System;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Identifies an absolute management path without a query or fragment.</summary>
/// <remarks>Paths compare ordinally, following RFC 3986 section 6.2.2.1.</remarks>
public readonly partial struct ResourceManagementPath
{
    /// <summary>Validates an absolute management path.</summary>
    /// <param name="value">The candidate path.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the path is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (!ResourceStateWire.IsValidManagementPath(value))
        {
            message = ErrorMessages.ResourceManagementPathInvalid;
            return false;
        }
        message = null;
        return true;
    }
}
