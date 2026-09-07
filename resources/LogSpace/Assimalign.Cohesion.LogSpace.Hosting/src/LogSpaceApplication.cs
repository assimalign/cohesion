using System;

using Assimalign.Cohesion.LogSpace;

namespace Assimalign.Cohesion.LogSpace.Hosting;

/// <summary>
/// Creates LogSpace application builders.
/// </summary>
public static class LogSpaceApplication
{
    /// <summary>
    /// Creates a builder for a LogSpace application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the LogSpace application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static ILogSpaceApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new LogSpaceApplicationBuilder(args);
    }
}
