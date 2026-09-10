using System;
using System.Reflection;

using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

/// <summary>
/// Creates identity hub application builders.
/// </summary>
public static class IdentityHubApplication
{
    /// <summary>
    /// Creates a builder for an identity hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the identity hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static IIdentityHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Assembly resourceAssembly = Assembly.GetEntryAssembly() ?? typeof(IdentityHubApplication).Assembly;
        return new IdentityHubApplicationBuilder(args, resourceAssembly);
    }

    internal static IIdentityHubApplicationBuilder CreateBuilder(
        string[] args,
        Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        return new IdentityHubApplicationBuilder(args, resourceAssembly);
    }
}
