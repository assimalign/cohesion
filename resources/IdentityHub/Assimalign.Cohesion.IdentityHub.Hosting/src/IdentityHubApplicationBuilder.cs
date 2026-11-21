using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;
using Assimalign.Cohesion.IdentityHub.Hosting.Internal;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

/// <summary>
/// Composes an IdentityHub application and its hosting services.
/// </summary>
/// <remarks>
/// Every dependency the application runs with is a registration in <see cref="Services"/>:
/// declared audiences and clients, the store, the users and groups defined in code, and the
/// lifecycle services (<see cref="IHostService"/>). <see cref="Build"/> closes registration,
/// creates the service provider once, validates the declared clients against the declared
/// audiences, creates the directory over the store, and resolves the lifecycle services once, in
/// registration order; the identity endpoint is always the last of them.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("IdentityHubApplicationBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class IdentityHubApplicationBuilder : IIdentityHubApplicationBuilder
{
    private const string DefaultEndpoint = "https://127.0.0.1:8443";

    private readonly string[] _args;
    private readonly string _environmentName;
    private readonly string _contentRootPath;
    private readonly IdentityHubApplicationContext _context;
    private bool _isBuilt;

    internal IdentityHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        _args = (string[])args.Clone();
        Services = new ServiceProviderBuilder(new ServiceProviderOptions
        {
            EnableDynamicCode = false,
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        _environmentName = AppEnvironment.GetEnvironmentName();
        _contentRootPath = AppContext.BaseDirectory;
        _context = new IdentityHubApplicationContext(
            _environmentName,
            System.IO.FileSystemPath.Parse(_contentRootPath));
    }

    /// <summary>
    /// Gets the application's service registrations.
    /// </summary>
    /// <remarks>
    /// Registration closes when <see cref="Build"/> runs; a later registration throws
    /// <see cref="InvalidOperationException"/>. Every <see cref="IHostService"/> registration joins
    /// the application lifecycle in registration order, ahead of the identity endpoint. Register
    /// factory or instance services: the provider is created without dynamic code. A
    /// factory-created service is owned by the application and disposed with it; an instance stays
    /// owned by its caller.
    /// </remarks>
    public ServiceProviderBuilder Services { get; }

    /// <summary>
    /// Declares an audience for access tokens issued by this identity hub.
    /// </summary>
    /// <remarks>
    /// The audience is registered in <see cref="Services"/>.
    /// </remarks>
    /// <param name="audience">The exact audience identifier.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="audience"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The audience is already declared or the application has been built.</exception>
    public IdentityHubApplicationBuilder AddAudience(string audience)
    {
        EnsureNotBuilt();
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ImplementationInstance is IdentityHubAudience declared &&
                string.Equals(declared.Value, audience, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.AudienceAlreadyDeclared, audience));
            }
        }

        Services.AddSingleton(new IdentityHubAudience(audience));
        return this;
    }

    /// <summary>
    /// Registers an OAuth client in the identity hub's code-first configuration.
    /// </summary>
    /// <remarks>
    /// The client registration is added to <see cref="Services"/>; its audiences are validated
    /// against the declared audiences when the application is built.
    /// </remarks>
    /// <param name="clientId">The exact client identifier.</param>
    /// <param name="configure">The callback that configures grants, credentials, and audiences.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="clientId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The client is already registered or the application has been built.</exception>
    public IdentityHubApplicationBuilder AddClient(
        string clientId,
        Action<IdentityHubClientOptions> configure)
    {
        EnsureNotBuilt();
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(configure);
        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ImplementationInstance is IdentityHubClientRegistration registered &&
                string.Equals(registered.ClientId, clientId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.ClientAlreadyRegistered, clientId));
            }
        }

        var options = new IdentityHubClientOptions();
        configure.Invoke(options);
        Services.AddSingleton(IdentityHubClientRegistration.Create(clientId, options));
        return this;
    }

    /// <summary>
    /// Sets the store that holds the identity objects created while the hub runs.
    /// </summary>
    /// <remarks>
    /// The store is registered in <see cref="Services"/> and stays owned by the caller. An
    /// application with no store keeps those objects in memory, in an
    /// <see cref="InMemoryIdentityHubStore"/> it owns: they are gone when it stops, and two nodes
    /// do not share them. The objects defined in code never reach the store.
    /// </remarks>
    /// <param name="store">The store.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The application already has a store or has been built.</exception>
    public IdentityHubApplicationBuilder AddStore(IIdentityHubStore store)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(store);
        EnsureNoStore();

        Services.AddSingleton<IIdentityHubStore>(store);
        return this;
    }

    /// <summary>
    /// Sets the store that holds the identity objects created while the hub runs, created when
    /// the application is built.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once, when the application is built, and receives the application's
    /// context. The application owns the store it returns: a store that is disposable is disposed
    /// with the application. <see cref="IdentityHubApplicationContext.Directory"/> is not
    /// available to the factory, because the directory is created over the store.
    /// </remarks>
    /// <param name="factory">The factory that creates the store.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The application already has a store or has been built, or the factory returns
    /// <see langword="null"/> during build.
    /// </exception>
    public IdentityHubApplicationBuilder AddStore(Func<IdentityHubApplicationContext, IIdentityHubStore> factory)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(factory);
        EnsureNoStore();

        Services.AddSingleton<IIdentityHubStore>(_ => factory.Invoke(_context)
            ?? throw new InvalidOperationException(ErrorMessages.StoreFactoryReturnedNull));
        return this;
    }

    /// <summary>
    /// Defines a user in code. The application serves it locked.
    /// </summary>
    /// <remarks>
    /// The user is registered in <see cref="Services"/>. A user stored under the same identifier,
    /// which a user created under the same name has, is not served from then on: the one defined
    /// here is.
    /// </remarks>
    /// <param name="user">The user.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="user"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A user with the same identifier or the same name is already defined, or the application has
    /// been built.
    /// </exception>
    public IdentityHubApplicationBuilder AddUser(IdentityUser user)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(user);
        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ImplementationInstance is not IdentityUser defined)
            {
                continue;
            }

            if (defined.Id.Equals(user.Id))
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.UserAlreadyDefined, Describe(user)));
            }

            if (defined.Name.Equals(user.Name))
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture,
                    ErrorMessages.UserNameAlreadyDefined,
                    user.Name.Value,
                    Describe(defined)));
            }
        }

        Services.AddSingleton(user);
        return this;
    }

    /// <summary>
    /// Defines a group in code. The application serves it locked.
    /// </summary>
    /// <remarks>
    /// The group is registered in <see cref="Services"/>. A group stored under the same
    /// identifier, which a group created under the same name has, is not served from then on: the
    /// one defined here is.
    /// </remarks>
    /// <param name="group">The group.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A group with the same identifier or the same name is already defined, or the application
    /// has been built.
    /// </exception>
    public IdentityHubApplicationBuilder AddGroup(IdentityGroup group)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(group);
        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ImplementationInstance is not IdentityGroup defined)
            {
                continue;
            }

            if (defined.Id.Equals(group.Id))
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.GroupAlreadyDefined, Describe(group)));
            }

            if (defined.Name.Equals(group.Name))
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.CurrentCulture,
                    ErrorMessages.GroupNameAlreadyDefined,
                    group.Name.Value,
                    Describe(defined)));
            }
        }

        Services.AddSingleton(group);
        return this;
    }

    /// <summary>
    /// Registers a host service with the identity hub application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order. The
    /// service is registered in <see cref="Services"/> and stays owned by the caller.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The application has been built.</exception>
    public IdentityHubApplicationBuilder AddService(IHostService service)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(service);

        Services.AddSingleton<IHostService>(service);
        return this;
    }

    /// <summary>
    /// Registers a host service factory with the identity hub application.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once, when the application is built, and receives the application's
    /// final host context. The application owns and disposes the service it returns, which follows
    /// registration order.
    /// </remarks>
    /// <param name="factory">The factory that creates the host service.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The application has been built, or the factory returns <see langword="null"/> during build.</exception>
    public IdentityHubApplicationBuilder AddService(Func<IdentityHubApplicationContext, IHostService> factory)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(factory);

        Services.AddSingleton<IHostService>(_ => factory.Invoke(_context)
            ?? throw new InvalidOperationException(ErrorMessages.ServiceFactoryReturnedNull));
        return this;
    }

    /// <summary>
    /// Builds the identity hub application.
    /// </summary>
    /// <returns>The configured identity hub application.</returns>
    /// <exception cref="InvalidOperationException">
    /// The builder has already built an application, a registered client is invalid, or a registered
    /// store or host service factory returns <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A command-line endpoint or data option is invalid or missing its value.</exception>
    public IdentityHubApplication Build()
    {
        EnsureNotBuilt();

        Uri endpoint = ResolveEndpoint();
        string dataPath = ResolveDataPath();
        Directory.CreateDirectory(dataPath);

        Services.AddSingleton<IdentityHubRegistrations>(serviceProvider => IdentityHubRegistrations.Create(
            serviceProvider.GetRequiredService<IEnumerable<IdentityHubAudience>>(),
            serviceProvider.GetRequiredService<IEnumerable<IdentityHubClientRegistration>>()));

        // An application with no store keeps the objects created at run time in memory. The
        // factory makes the application the owner of that store.
        if (!HasStore())
        {
            Services.AddSingleton<IIdentityHubStore>(_ => new InMemoryIdentityHubStore());
        }

        // The objects defined in code are checked against each other when the directory is created.
        Services.AddSingleton<IIdentityHubDirectory>(serviceProvider =>
        {
            var descriptor = new IdentityHubDirectoryDescriptor();
            foreach (IdentityUser user in serviceProvider.GetRequiredService<IEnumerable<IdentityUser>>())
            {
                descriptor.Users.Add(user);
            }

            foreach (IdentityGroup group in serviceProvider.GetRequiredService<IEnumerable<IdentityGroup>>())
            {
                descriptor.Groups.Add(group);
            }

            return IdentityHubDirectory.Create(serviceProvider.GetRequiredService<IIdentityHubStore>(), descriptor);
        });

        // Registered after every AddService call, so the endpoint starts last and stops first.
        Services.AddSingleton<IHostService>(serviceProvider =>
        {
            IdentityHubRegistrations registrations = serviceProvider.GetRequiredService<IdentityHubRegistrations>();
            return new IdentityEndpointService(
                endpoint,
                dataPath,
                registrations.Clients,
                _environmentName,
                _context);
        });
        Services.AddSingleton<IHostEnvironment>(_context.Environment);
        Services.AddSingleton<IIdentityHubApplicationContext>(_context);

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
            // The declared clients are validated before any lifecycle service is created.
            _ = _context.ServiceProvider.GetRequiredService<IdentityHubRegistrations>();

            // The directory exists before any lifecycle service is created, so a service factory
            // reads it from the context.
            _context.SetDirectory(_context.ServiceProvider.GetRequiredService<IIdentityHubDirectory>());
            _context.ResolveHostedServices();

            return new IdentityHubApplication(
                new IdentityHubApplicationOptions { Environment = _environmentName },
                _context);
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

    IIdentityHubApplicationBuilder IIdentityHubApplicationBuilder.AddAudience(string audience) => AddAudience(audience);

    IIdentityHubApplicationBuilder IIdentityHubApplicationBuilder.AddClient(
        string clientId,
        Action<IdentityHubClientOptions> configure) => AddClient(clientId, configure);

    IIdentityHubApplicationBuilder IIdentityHubApplicationBuilder.AddStore(IIdentityHubStore store) => AddStore(store);

    IIdentityHubApplicationBuilder IIdentityHubApplicationBuilder.AddStore(
        Func<IIdentityHubApplicationContext, IIdentityHubStore> factory)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(factory);

        return AddStore(context => factory.Invoke(context));
    }

    IIdentityHubApplicationBuilder IIdentityHubApplicationBuilder.AddUser(IdentityUser user) => AddUser(user);

    IIdentityHubApplicationBuilder IIdentityHubApplicationBuilder.AddGroup(IdentityGroup group) => AddGroup(group);

    IIdentityHubApplication IIdentityHubApplicationBuilder.Build() => Build();

    private static string Describe(IdentityUser user)
    {
        return string.Concat(user.Name.Value, " (", user.Id.ToString(), ")");
    }

    private static string Describe(IdentityGroup group)
    {
        return string.Concat(group.Name.Value, " (", group.Id.ToString(), ")");
    }

    private bool HasStore()
    {
        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ServiceType == typeof(IIdentityHubStore))
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureNoStore()
    {
        if (HasStore())
        {
            throw new InvalidOperationException(ErrorMessages.StoreAlreadyRegistered);
        }
    }

    private void EnsureNotBuilt()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationAlreadyBuilt);
        }
    }

    private Uri ResolveEndpoint()
    {
        return ParseArgument("--endpoint") is string configured
            ? ParseEndpoint(configured)
            : new Uri(DefaultEndpoint, UriKind.Absolute);
    }

    private string ResolveDataPath()
    {
        string? configured = ParseArgument("--data");
        return Path.GetFullPath(configured ?? Path.Combine(_contentRootPath, "data"));
    }

    private string? ParseArgument(string name)
    {
        for (int index = 0; index < _args.Length; index++)
        {
            string argument = _args[index];
            if (argument.StartsWith(name + "=", StringComparison.Ordinal))
            {
                string value = argument[(name.Length + 1)..];
                ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
                return value;
            }

            if (string.Equals(argument, name, StringComparison.Ordinal))
            {
                if (index + 1 >= _args.Length)
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.OptionValueRequired, name),
                        name);
                }

                string value = _args[index + 1];
                ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
                return value;
            }
        }

        return null;
    }

    private static Uri ParseEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint) ||
            endpoint.IsFile ||
            endpoint.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(ErrorMessages.EndpointInvalid, nameof(value));
        }

        return endpoint;
    }

    private sealed class DebugView
    {
        private readonly IdentityHubApplicationBuilder _target;

        public DebugView(IdentityHubApplicationBuilder value)
        {
            _target = value;
        }

        public ServiceProviderBuilder Services => _target.Services;
    }
}
