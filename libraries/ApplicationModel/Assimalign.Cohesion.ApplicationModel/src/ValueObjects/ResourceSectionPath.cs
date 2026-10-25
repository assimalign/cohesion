using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies a configuration section of a resource's runtime surface by its configuration path, such as
/// <c>Web:Cors</c>.
/// </summary>
/// <remarks>
/// A path is one or more segments of ASCII letters, digits, periods, underscores or hyphens separated by single
/// colons, at most 256 characters. Paths compare ordinally: a deployment names a section with exactly the spelling
/// the resource declared.
/// </remarks>
public partial struct ResourceSectionPath
{
    /// <summary>Validates the configuration path grammar.</summary>
    /// <param name="value">The candidate path.</param>
    /// <param name="message">The rejection reason, or null when valid.</param>
    /// <returns>Whether the path is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (ResourceSurfaceGrammar.IsSectionPath(value))
        {
            message = null;
            return true;
        }

        message = ErrorMessages.ResourceSectionPathInvalid;
        return false;
    }
}
