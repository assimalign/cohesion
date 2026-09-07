using System;

using Assimalign.Cohesion.Rezolvr;

namespace Assimalign.Cohesion.Rezolvr.Hosting;

/// <summary>
/// Creates Rezolvr application builders.
/// </summary>
public static class RezolvrApplication
{
    /// <summary>
    /// Creates a builder for a Rezolvr application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the Rezolvr application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static IRezolvrApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new RezolvrApplicationBuilder(args);
    }
}
