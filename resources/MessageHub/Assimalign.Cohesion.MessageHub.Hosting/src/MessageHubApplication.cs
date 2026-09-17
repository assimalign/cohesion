using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.MessageHub;

namespace Assimalign.Cohesion.MessageHub.Hosting;

/// <summary>
/// Hosts a MessageHub application and its ordered service lifecycle.
/// </summary>
public sealed class MessageHubApplication : Host<MessageHubApplicationContext>, IMessageHubApplication
{
    private readonly MessageHubApplicationContext _context;

    internal MessageHubApplication(
        MessageHubApplicationOptions options,
        MessageHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override MessageHubApplicationContext Context => _context;

    IMessageHubApplicationContext IMessageHubApplication.Context => _context;

    Task IMessageHubApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task IMessageHubApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for a message hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the message hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static MessageHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(MessageHubApplication).Assembly);
    }

    internal static MessageHubApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new MessageHubApplicationBuilder(args, resourceAssembly);
    }
}
