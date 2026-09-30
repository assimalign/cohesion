using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Hosting.Internal;
using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.SecretStore.Hosting;

/// <summary>
/// Composes a SecretStore application and its hosting services.
/// </summary>
/// <remarks>
/// Every dependency the application runs with is a registration in <see cref="Services"/>:
/// declared secrets and certificate-authority options, the data-protection provider, the secret
/// repository, the trust-grant store, the certificate authority, and the lifecycle services
/// (<see cref="IHostService"/>). <see cref="Build"/> closes registration, creates the service
/// provider once, and resolves the lifecycle services once, in registration order; the secrets
/// endpoint is always the last of them. The application disposes every collaborator the provider
/// created when it is disposed.
/// </remarks>
public sealed class SecretStoreApplicationBuilder : ISecretStoreApplicationBuilder
{
    private const string DefaultEndpoint = "https://127.0.0.1:8443";

    private readonly string[] _args;
    private readonly string _environmentName;
    private readonly SecretStoreApplicationContext _context;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext _resourceContext;
    private bool _isBuilt;

    internal SecretStoreApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        _args = (string[])args.Clone();
        Services = new ServiceProviderBuilder(new ServiceProviderOptions
        {
            EnableDynamicCode = false,
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _resourceContext = ResourceRuntime.Current;
        ILoggerFactory? loggerFactory = ResourceTelemetry.Configure(_resourceContext, out IHostService? telemetry);
        if (loggerFactory is not null)
        {
            Services.AddSingleton<ILoggerFactory>(loggerFactory);
        }
        if (telemetry is not null)
        {
            // Registered ahead of every AddService call, so telemetry starts first and stops last.
            Services.AddSingleton<IHostService>(telemetry);
        }
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException(
                "The registered SecretStore control-plane factory returned null.");
            Services.AddSingleton<IResourceControlPlane>(_controlPlane);
        }

        _environmentName = _resourceContext.EnvironmentName;
        _context = new SecretStoreApplicationContext(
            _environmentName,
            System.IO.FileSystemPath.Parse(_resourceContext.ContentRootPath));
    }

    /// <summary>
    /// Gets the application's service registrations.
    /// </summary>
    /// <remarks>
    /// Registration closes when <see cref="Build"/> runs; a later registration throws
    /// <see cref="InvalidOperationException"/>. Every <see cref="IHostService"/> registration joins
    /// the application lifecycle in registration order, ahead of the secrets endpoint. Register
    /// factory or instance services: the provider is created without dynamic code. A
    /// factory-created service is owned by the application and disposed with it; an instance stays
    /// owned by its caller.
    /// </remarks>
    public ServiceProviderBuilder Services { get; }

    /// <summary>
    /// Declares an initial secret at a logical store path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value seeds the path only when durable state does not already contain a version at
    /// that path. A restart therefore never rolls back a value that was rotated after seeding.
    /// </para>
    /// <para>
    /// Implementations snapshot <paramref name="value"/> during registration. Paths are compared
    /// using ordinal semantics and are not normalized. The declaration is registered in
    /// <see cref="Services"/>.
    /// </para>
    /// </remarks>
    /// <param name="path">The logical path at which to seed the secret.</param>
    /// <param name="value">The secret bytes to seed.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty or whitespace, or names an implementation-reserved path.
    /// </exception>
    /// <exception cref="InvalidOperationException">The path has already been declared on this builder.</exception>
    public SecretStoreApplicationBuilder AddSecret(
        string path,
        ReadOnlyMemory<byte> value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (string.Equals(path, "trusted-issuers.json", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Secret path 'trusted-issuers.json' is reserved for the trust-grant document.",
                nameof(path));
        }

        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ImplementationInstance is SecretSeed declared &&
                string.Equals(declared.Path, path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Secret path '{path}' is already declared.");
            }
        }

        Services.AddSingleton(new SecretSeed(path, value.ToArray()));
        return this;
    }

    /// <summary>
    /// Declares the certificate authority owned by this secret store.
    /// </summary>
    /// <remarks>
    /// Durable certificate-authority state always wins over composition-time seed material. On a
    /// first start, paired initial PEM material is used when supplied; otherwise a configured
    /// Platform endpoint selects gateway-mediated intermediate enrollment. When neither is configured,
    /// <see cref="CertificateAuthorityOptions.SelfSeedWhenNoPlatform"/> controls whether the store
    /// creates a self-signed development root. A failed configured Platform enrollment never
    /// silently falls back to an unrelated self-signed root. A snapshot of the options is
    /// registered in <see cref="Services"/>.
    /// </remarks>
    /// <param name="configure">An optional callback that configures the certificate authority.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// The common name is empty; the Platform enrollment endpoint is not absolute HTTPS; configured
    /// PEM material is empty; or an initial certificate and private key are not supplied together.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A certificate authority has already been declared, or no first-start authority source is
    /// enabled.
    /// </exception>
    public SecretStoreApplicationBuilder AddCertificateAuthority(
        Action<CertificateAuthorityOptions>? configure = null)
    {
        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ServiceType == typeof(CertificateAuthorityOptions))
            {
                throw new InvalidOperationException("A certificate authority is already declared.");
            }
        }

        var options = new CertificateAuthorityOptions();
        configure?.Invoke(options);
        ValidateCertificateAuthority(options);
        Services.AddSingleton(Snapshot(options));
        return this;
    }

    /// <summary>
    /// Adds an existing host service to the secret store application.
    /// </summary>
    /// <remarks>
    /// The service is registered in <see cref="Services"/> and stays owned by the caller.
    /// </remarks>
    /// <param name="service">The service to add.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public SecretStoreApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        Services.AddSingleton<IHostService>(service);
        return this;
    }

    /// <summary>
    /// Adds a host service factory that is invoked once, when the application is built.
    /// </summary>
    /// <remarks>
    /// The application owns and disposes the service the factory returns.
    /// </remarks>
    /// <param name="factory">The factory to invoke with the secret store host context.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returns <see langword="null"/>.</exception>
    public SecretStoreApplicationBuilder AddService(Func<SecretStoreApplicationContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Services.AddSingleton<IHostService>(_ => factory(_context)
            ?? throw new InvalidOperationException("A secret store service factory returned null."));
        return this;
    }

    /// <summary>
    /// Builds the secret store application.
    /// </summary>
    /// <returns>The configured secret store application.</returns>
    /// <exception cref="InvalidOperationException">
    /// The builder has already built an application, a registered service factory returns
    /// <see langword="null"/>, or the ambient endpoint or data mount cannot host the store.
    /// </exception>
    /// <exception cref="ArgumentException">A command-line endpoint or data option is empty or missing its value.</exception>
    public SecretStoreApplication Build()
    {
        InvalidOperationException.ThrowIf(_isBuilt, "The secret store application has already been built.");

        Uri endpoint = ResolveEndpoint();
        string dataPath = ResolveDataPath();
        Directory.CreateDirectory(dataPath);
        ProtectedFileStore.HardenKeyDirectory(dataPath);
        string keyRingPath = Path.Combine(dataPath, "key-ring");
        Directory.CreateDirectory(keyRingPath);
        ProtectedFileStore.HardenKeyDirectory(keyRingPath);

        Services.AddSingleton<IDataProtectionProvider>(_ => DataProtectionProvider.Create(
            KeyRepository.CreateFileSystem(keyRingPath),
            protectionOptions =>
            {
                protectionOptions.ApplicationDiscriminator =
                    $"SecretStore:{_resourceContext.ApplicationName ?? "standalone"}:{_resourceContext.ResourceName ?? "default"}";
                protectionOptions.KeyLifetime = TimeSpan.FromDays(90);
                protectionOptions.UnprotectGracePeriod = TimeSpan.FromDays(36500);
            }));
        Services.AddSingleton<SecretStoreRepository>(serviceProvider =>
        {
            var seeds = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
            foreach (SecretSeed seed in serviceProvider.GetRequiredService<IEnumerable<SecretSeed>>())
            {
                if (!seeds.TryAdd(seed.Path, seed.Value))
                {
                    throw new InvalidOperationException($"Secret path '{seed.Path}' is already declared.");
                }
            }

            return new SecretStoreRepository(
                dataPath,
                serviceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("secret-store", "secrets", "v1"),
                seeds);
        });
        Services.AddSingleton<TrustedIssuerStore>(serviceProvider => new TrustedIssuerStore(
            dataPath,
            serviceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("secret-store", "trusted-issuers", "v1")));
        Services.AddSingleton<CertificateAuthorityManager>(serviceProvider => new CertificateAuthorityManager(
            dataPath,
            serviceProvider.GetRequiredService<IDataProtectionProvider>(),
            serviceProvider.GetService<CertificateAuthorityOptions>() ?? new CertificateAuthorityOptions(),
            _resourceContext.ApplicationName,
            _resourceContext.ResourceName));

        // Registered after every AddService call, so the endpoint starts last and stops first.
        Services.AddSingleton<IHostService>(serviceProvider => new SecretsEndpointService(
            endpoint,
            serviceProvider.GetRequiredService<SecretStoreRepository>(),
            serviceProvider.GetRequiredService<TrustedIssuerStore>(),
            serviceProvider.GetRequiredService<CertificateAuthorityManager>(),
            _controlPlane,
            _resourceContext,
            _context));
        Services.AddSingleton<IHostEnvironment>(_context.Environment);
        Services.AddSingleton<ISecretStoreApplicationContext>(_context);

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

            var application = new SecretStoreApplication(
                new SecretStoreApplicationOptions { Environment = _environmentName },
                _context);
            if (_controlPlane is not null)
            {
                ResourceRuntime.HostBuilt(application, _controlPlane);
            }

            return application;
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

    ISecretStoreApplicationBuilder ISecretStoreApplicationBuilder.AddSecret(
        string path,
        ReadOnlyMemory<byte> value) => AddSecret(path, value);

    ISecretStoreApplicationBuilder ISecretStoreApplicationBuilder.AddCertificateAuthority(
        Action<CertificateAuthorityOptions>? configure) => AddCertificateAuthority(configure);

    ISecretStoreApplication ISecretStoreApplicationBuilder.Build() => Build();

    private Uri ResolveEndpoint()
    {
        if (_controlPlane?.ObservedEndpoints.TryGetValue("api", out Uri? observed) is true)
        {
            return observed;
        }

        if (_resourceContext.Endpoints.TryGetValue("api", out Uri? supplied))
        {
            return supplied;
        }

        return ParseArgument("--endpoint") is string configured
            ? ParseEndpoint(configured)
            : new Uri(DefaultEndpoint, UriKind.Absolute);
    }

    private string ResolveDataPath()
    {
        if (_resourceContext.Mounts.TryGetValue("data", out ResourceMount? mount))
        {
            if (string.IsNullOrWhiteSpace(mount.Path))
            {
                throw new InvalidOperationException(
                    "The SecretStore 'data' volume must expose a file-system path.");
            }

            return Path.GetFullPath(mount.Path);
        }

        string? configured = ParseArgument("--data");
        string path = configured ?? Path.Combine(_resourceContext.ContentRootPath, "data");
        return Path.GetFullPath(path);
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
                        $"Command-line option '{name}' requires a value.",
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
            !endpoint.IsAbsoluteUri ||
            endpoint.IsFile ||
            endpoint.Port <= 0 ||
            string.IsNullOrWhiteSpace(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"The SecretStore endpoint '{value}' must be an absolute HTTP or HTTPS endpoint.");
        }

        return endpoint;
    }

    private static void ValidateCertificateAuthority(CertificateAuthorityOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CommonName);
        if (options.PlatformEnrollmentEndpoint is { } endpoint &&
            (!endpoint.IsAbsoluteUri ||
             !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "The Platform enrollment endpoint must be an absolute HTTPS endpoint.",
                nameof(options));
        }

        if ((options.PlatformCertificate.HasValue && options.PlatformCertificate.Value.IsEmpty) ||
            (options.InitialCertificate.HasValue && options.InitialCertificate.Value.IsEmpty) ||
            (options.InitialPrivateKey.HasValue && options.InitialPrivateKey.Value.IsEmpty))
        {
            throw new ArgumentException("Supplied certificate and private-key values must not be empty.", nameof(options));
        }

        if (options.InitialCertificate.HasValue != options.InitialPrivateKey.HasValue)
        {
            throw new ArgumentException(
                "InitialCertificate and InitialPrivateKey must be supplied together.",
                nameof(options));
        }

        if (!options.InitialCertificate.HasValue &&
            options.PlatformEnrollmentEndpoint is null &&
            !options.SelfSeedWhenNoPlatform)
        {
            throw new InvalidOperationException(
                "A certificate authority requires initial material, a Platform enrollment endpoint, or standalone self-seeding.");
        }
    }

    private static CertificateAuthorityOptions Snapshot(CertificateAuthorityOptions source)
    {
        return new CertificateAuthorityOptions
        {
            CommonName = source.CommonName,
            SelfSeedWhenNoPlatform = source.SelfSeedWhenNoPlatform,
            PlatformEnrollmentEndpoint = source.PlatformEnrollmentEndpoint,
            PlatformCertificate = Copy(source.PlatformCertificate),
            InitialCertificate = Copy(source.InitialCertificate),
            InitialPrivateKey = Copy(source.InitialPrivateKey),
        };
    }

    private static ReadOnlyMemory<byte>? Copy(ReadOnlyMemory<byte>? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return new ReadOnlyMemory<byte>(value.Value.ToArray());
    }
}
