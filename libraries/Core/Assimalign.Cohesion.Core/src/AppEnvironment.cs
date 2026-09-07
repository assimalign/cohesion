using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion;

/// <summary>
/// Resolves the application environment name from the Cohesion runtime contract.
/// </summary>
public static partial class AppEnvironment
{
    /// <summary>
    /// Gets the environment name for the current process.
    /// </summary>
    /// <returns>
    /// The Cohesion environment override, the .NET environment fallback, or
    /// <see cref="Keys.DefaultEnvironmentName"/> when neither variable is set.
    /// </returns>
    public static string GetEnvironmentName()
    {
        return GetEnvironmentNameCore(environment: null);
    }

    /// <summary>
    /// Gets the environment name from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <returns>
    /// The Cohesion environment override, the .NET environment fallback, or
    /// <see cref="Keys.DefaultEnvironmentName"/> when neither variable is present.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    public static string GetEnvironmentName(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return GetEnvironmentNameCore(environment);
    }

    private static string GetEnvironmentNameCore(IDictionary<string, string?>? environment)
    {
        string? cohesionEnvironment = environment is null
            ? ResourceEnvironment.GetValue(Keys.EnvironmentKey)
            : ResourceEnvironment.GetValue(environment, Keys.EnvironmentKey);

        string? dotNetEnvironment = environment is null
            ? ResourceEnvironment.GetValue(Keys.DotNetEnvironmentKey)
            : ResourceEnvironment.GetValue(environment, Keys.DotNetEnvironmentKey);

        return cohesionEnvironment ?? dotNetEnvironment ?? Keys.DefaultEnvironmentName;
    }
}
