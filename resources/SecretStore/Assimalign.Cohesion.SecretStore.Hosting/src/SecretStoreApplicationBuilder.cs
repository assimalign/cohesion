using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.SecretStore;

namespace Assimalign.Cohesion.SecretStore.Hosting;

internal sealed class SecretStoreApplicationBuilder : ISecretStoreApplicationBuilder
{
    private const string DefaultEndpoint = "https://127.0.0.1:8443";

    private readonly string[] _args;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext _resourceContext;
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _secrets = new(StringComparer.Ordinal);
    private readonly List<Func<IHostContext, IHostService>> _serviceFactories = [];
    private CertificateAuthorityOptions? _certificateAuthority;
    private bool _isBuilt;

    internal SecretStoreApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        _args = (string[])args.Clone();
        _resourceContext = ResourceRuntime.Current;
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException(
                "The registered SecretStore control-plane factory returned null.");
        }
    }

    public ISecretStoreApplicationBuilder AddSecret(
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

        if (!_secrets.TryAdd(path, value.ToArray()))
        {
            throw new InvalidOperationException($"Secret path '{path}' is already declared.");
        }

        return this;
    }

    public ISecretStoreApplicationBuilder AddCertificateAuthority(
        Action<CertificateAuthorityOptions>? configure = null)
    {
        if (_certificateAuthority is not null)
        {
            throw new InvalidOperationException("A certificate authority is already declared.");
        }

        var options = new CertificateAuthorityOptions();
        configure?.Invoke(options);
        ValidateCertificateAuthority(options);
        _certificateAuthority = Snapshot(options);
        return this;
    }

    public ISecretStoreApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceFactories.Add(_ => service);
        return this;
    }

    public ISecretStoreApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceFactories.Add(factory);
        return this;
    }

    public ISecretStoreApplication Build()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException("The secret store application has already been built.");
        }

        string environmentName = _resourceContext.EnvironmentName;
        var options = new SecretStoreApplicationOptions { Environment = environmentName };
        var context = new SecretStoreApplicationContext(environmentName);
        var hostedServices = new IHostService[_serviceFactories.Count + 1];
        for (int index = 0; index < _serviceFactories.Count; index++)
        {
            hostedServices[index] = _serviceFactories[index](context)
                ?? throw new InvalidOperationException("A secret store service factory returned null.");
        }

        Uri endpoint = ResolveEndpoint();
        string dataPath = ResolveDataPath();
        Directory.CreateDirectory(dataPath);
        ProtectedFileStore.HardenKeyDirectory(dataPath);
        string keyRingPath = Path.Combine(dataPath, "key-ring");
        Directory.CreateDirectory(keyRingPath);
        ProtectedFileStore.HardenKeyDirectory(keyRingPath);
        IDataProtectionProvider protectionProvider = DataProtectionProvider.Create(
            KeyRepository.CreateFileSystem(keyRingPath),
            protectionOptions =>
            {
                protectionOptions.ApplicationDiscriminator =
                    $"SecretStore:{_resourceContext.ApplicationName ?? "standalone"}:{_resourceContext.ResourceName ?? "default"}";
                protectionOptions.KeyLifetime = TimeSpan.FromDays(90);
                protectionOptions.UnprotectGracePeriod = TimeSpan.FromDays(36500);
            });
        var repository = new SecretStoreRepository(
            dataPath,
            protectionProvider.CreateProtector("secret-store", "secrets", "v1"),
            _secrets);
        var trustedIssuers = new TrustedIssuerStore(
            dataPath,
            protectionProvider.CreateProtector("secret-store", "trusted-issuers", "v1"));
        var authority = new CertificateAuthorityManager(
            dataPath,
            protectionProvider,
            _certificateAuthority ?? new CertificateAuthorityOptions(),
            _resourceContext.ApplicationName,
            _resourceContext.ResourceName);
        var endpointService = new SecretsEndpointService(
            endpoint,
            repository,
            trustedIssuers,
            authority,
            _controlPlane,
            _resourceContext,
            context);
        hostedServices[^1] = endpointService;

        context.SetHostedServices(hostedServices);
        _isBuilt = true;
        var application = new SecretStoreApplicationHost(options, context, endpointService);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }

        return application;
    }

    IHost IHostBuilder.Build() => Build();

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
