using System;

namespace Assimalign.Cohesion.MessageHub.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.MessageHub.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the message broker resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class MessageHubApplication : Host<MessageHubApplicationContext>
{
    private readonly MessageHubApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageHubApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public MessageHubApplication(MessageHubApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new MessageHubApplicationContext(options, new IHostService[]
        {
            new JournalFlushService(),
            new BrokerEndpointService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override MessageHubApplicationContext Context => _context;
}