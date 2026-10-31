using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.EmailHub;
using Assimalign.Cohesion.EmailHub.Hosting.Internal;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.EmailHub.Hosting;

/// <summary>
/// Composes an EmailHub application and its hosting services.
/// </summary>
/// <remarks>
/// Every dependency the application runs with is a registration in <see cref="Services"/>.
/// <see cref="Build"/> closes registration, creates the service provider once, and resolves the
/// lifecycle services (<see cref="IHostService"/>) once, in registration order.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("EmailHubApplicationBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class EmailHubApplicationBuilder : IEmailHubApplicationBuilder
{
    private readonly EmailHubApplicationContext _context;

    private bool _isBuilt;

    internal EmailHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Services = new ServiceProviderBuilder(new ServiceProviderOptions
        {
            EnableDynamicCode = false,
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        _context = new EmailHubApplicationContext();
    }

    /// <summary>
    /// Gets the application's service registrations.
    /// </summary>
    /// <remarks>
    /// Registration closes when <see cref="Build"/> runs; a later registration throws
    /// <see cref="InvalidOperationException"/>. Every <see cref="IHostService"/> registration joins
    /// the application lifecycle in registration order. Register factory or instance services: the
    /// provider is created without dynamic code. A factory-created service is owned by the
    /// application and disposed with it; an instance stays owned by its caller.
    /// </remarks>
    public ServiceProviderBuilder Services { get; }

    /// <summary>
    /// Registers a host service with the email hub application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order. The
    /// service is registered in <see cref="Services"/> and stays owned by the caller.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public EmailHubApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        Services.AddSingleton<IHostService>(service);
        return this;
    }

    /// <summary>
    /// Registers a host service factory with the email hub application.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once, when the application is built, and receives the application's
    /// final host context. The application owns and disposes the service it returns, which follows
    /// registration order.
    /// </remarks>
    /// <param name="factory">The factory that creates the host service.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The factory returns <see langword="null"/> when the application is built.</exception>
    public EmailHubApplicationBuilder AddService(Func<EmailHubApplicationContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Services.AddSingleton<IHostService>(_ => factory.Invoke(_context)
            ?? throw new InvalidOperationException("The email hub application service factory returned null."));
        return this;
    }

    /// <summary>
    /// Builds the email hub application.
    /// </summary>
    /// <returns>The configured email hub application.</returns>
    /// <exception cref="InvalidOperationException">
    /// The application was already built, or a registered host service factory returns <see langword="null"/>.
    /// </exception>
    public EmailHubApplication Build()
    {
        InvalidOperationException.ThrowIf(_isBuilt, "The email hub application has already been built.");

        Services.AddSingleton<IHostEnvironment>(_context.Environment);
        Services.AddSingleton<IEmailHubApplicationContext>(_context);

        _isBuilt = true;

        // Registration closes here. The provider copies the registrations, so one added after
        // Build would silently never reach it; the read-only container turns that into an error.
        if (Services.Container is ServiceContainer container)
        {
            container.MakeReadOnly();
        }

        _context.SetServiceProvider(((IServiceProviderBuilder)Services).Build());
        try
        {
            _context.ResolveHostedServices();

            return new EmailHubApplication(new EmailHubApplicationOptions(), _context);
        }
        catch (Exception exception)
        {
            // The provider owns every service a factory created before the failure.
            try
            {
                Task.Run(() => _context.DisposeServiceProviderAsync().AsTask()).GetAwaiter().GetResult();
            }
            catch (Exception disposalException)
            {
                throw new AggregateException(exception, disposalException);
            }

            throw;
        }
    }

    IEmailHubApplication IEmailHubApplicationBuilder.Build() => Build();

    private sealed class DebugView
    {
        private readonly EmailHubApplicationBuilder _target;

        public DebugView(EmailHubApplicationBuilder value)
        {
            _target = value;
        }

        public ServiceProviderBuilder Services => _target.Services;
    }
}
