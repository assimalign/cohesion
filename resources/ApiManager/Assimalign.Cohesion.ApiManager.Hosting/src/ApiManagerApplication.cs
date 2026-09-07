using System;

using Assimalign.Cohesion.ApiManager;

namespace Assimalign.Cohesion.ApiManager.Hosting;

/// <summary>
/// Creates API manager application builders.
/// </summary>
public static class ApiManagerApplication
{
    /// <summary>
    /// Creates a builder for an API manager application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the API manager application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static IApiManagerApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new ApiManagerApplicationBuilder(args);
    }
}
