using System;
using System.Reflection;

using Assimalign.Cohesion.SecretStore;

namespace Assimalign.Cohesion.SecretStore.Hosting;

/// <summary>
/// Creates secret store application builders.
/// </summary>
public static class SecretStoreApplication
{
    /// <summary>
    /// Creates a builder for a secret store application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the secret store application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static ISecretStoreApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Assembly resourceAssembly = Assembly.GetEntryAssembly() ?? typeof(SecretStoreApplication).Assembly;
        return new SecretStoreApplicationBuilder(args, resourceAssembly);
    }

    internal static ISecretStoreApplicationBuilder CreateBuilder(
        string[] args,
        Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        return new SecretStoreApplicationBuilder(args, resourceAssembly);
    }
}
