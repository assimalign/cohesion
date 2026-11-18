using System;
using System.Globalization;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// A calendar API version, <c>yyyy-MM-dd</c> with an optional <c>-preview</c> suffix, such as
/// <c>2026-12-01</c>.
/// </summary>
/// <remarks>
/// Every resource type, every typed platform extension, and every platform contract carries its
/// own version, and each versions independently.
/// </remarks>
public partial struct ApiVersion
{
    private const string previewSuffix = "-preview";

    /// <summary>
    /// Gets a value indicating whether this is a preview version.
    /// </summary>
    public bool IsPreview => Value is not null && Value.EndsWith(previewSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Determines whether <paramref name="value"/> is a valid API version.
    /// </summary>
    /// <param name="value">The candidate version.</param>
    /// <param name="message">The reason the version is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a real calendar date with an optional preview suffix.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        var date = value;
        if (date is not null && date.EndsWith(previewSuffix, StringComparison.Ordinal))
        {
            date = date[..^previewSuffix.Length];
        }

        if (date is null || !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            message = ErrorMessages.ApiVersionInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
