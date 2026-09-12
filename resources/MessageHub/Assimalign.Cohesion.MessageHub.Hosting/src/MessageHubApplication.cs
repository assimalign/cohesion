using System;
using System.Reflection;

using Assimalign.Cohesion.MessageHub;

namespace Assimalign.Cohesion.MessageHub.Hosting;

/// <summary>
/// Creates message hub application builders.
/// </summary>
public static class MessageHubApplication
{
    /// <summary>
    /// Creates a builder for a message hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the message hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static IMessageHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(MessageHubApplication).Assembly);
    }

    internal static IMessageHubApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new MessageHubApplicationBuilder(args, resourceAssembly);
    }
}
