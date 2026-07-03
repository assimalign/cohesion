using System;

namespace Assimalign.Cohesion.EmailHub.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.EmailHub.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the mail hub resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class EmailHubApplication : Host<EmailHubApplicationContext>
{
    private readonly EmailHubApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailHubApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public EmailHubApplication(EmailHubApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new EmailHubApplicationContext(options, new IHostService[]
        {
            new MailEndpointService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override EmailHubApplicationContext Context => _context;
}