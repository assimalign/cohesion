using System;
using System.Reflection;

using Assimalign.Cohesion.EmailHub;

namespace Assimalign.Cohesion.EmailHub.Hosting;

/// <summary>
/// Creates email hub application builders.
/// </summary>
public static class EmailHubApplication
{
    /// <summary>
    /// Creates a builder for an email hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the email hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static IEmailHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(EmailHubApplication).Assembly);
    }

    internal static IEmailHubApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new EmailHubApplicationBuilder(args, resourceAssembly);
    }
}
