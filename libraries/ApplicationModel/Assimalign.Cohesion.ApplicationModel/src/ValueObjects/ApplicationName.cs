using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies a Cohesion application and its ownership boundary.
/// </summary>
public partial struct ApplicationName
{
    internal partial class ApplicationNameJsonConverter
    {
    }

    /// <summary>
    /// Creates an application name from its textual representation.
    /// </summary>
    /// <param name="value">The application name.</param>
    /// <returns>The parsed application name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Structural validation is performed by <see cref="IApplicationBuilder.Build"/>, after
    /// all builder configuration has been applied.
    /// </remarks>
    public static ApplicationName Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }
}
