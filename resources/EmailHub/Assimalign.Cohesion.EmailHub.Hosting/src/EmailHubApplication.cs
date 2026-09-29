using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.EmailHub;
using Assimalign.Cohesion.EmailHub.Hosting.Internal;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.EmailHub.Hosting;

/// <summary>
/// Hosts an EmailHub application and its ordered service lifecycle.
/// </summary>
public sealed class EmailHubApplication : Host<EmailHubApplicationContext>, IEmailHubApplication
{
    private readonly EmailHubApplicationContext _context;

    internal EmailHubApplication(
        EmailHubApplicationOptions options,
        EmailHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override EmailHubApplicationContext Context => _context;

    IEmailHubApplicationContext IEmailHubApplication.Context => _context;

    Task IEmailHubApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task IEmailHubApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for an email hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the email hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static EmailHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(EmailHubApplication).Assembly);
    }

    internal static EmailHubApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new EmailHubApplicationBuilder(args, resourceAssembly);
    }
}
