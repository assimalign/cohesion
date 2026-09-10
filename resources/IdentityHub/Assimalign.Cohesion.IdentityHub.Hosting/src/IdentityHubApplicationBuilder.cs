using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

internal sealed class IdentityHubApplicationBuilder : IIdentityHubApplicationBuilder
{
    private const string DefaultEndpoint = "https://127.0.0.1:8443";

    private readonly string[] _args;
    private readonly Dictionary<string, IdentityHubClientRegistration> _clients = new(StringComparer.Ordinal);
    private readonly IResourceControlPlane? _controlPlane;
    private readonly HashSet<string> _audiences = new(StringComparer.Ordinal);
    private readonly ResourceContext _resourceContext;
    private readonly List<Func<IHostContext, IHostService>> _serviceRegistrations = new();
    private bool _isBuilt;

    internal IdentityHubApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        _args = (string[])args.Clone();
        _resourceContext = ResourceRuntime.Current;
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException(
                "The registered IdentityHub control-plane factory returned null.");
        }
    }

    public IIdentityHubApplicationBuilder AddAudience(string audience)
    {
        EnsureNotBuilt();
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        if (!_audiences.Add(audience))
        {
            throw new InvalidOperationException($"IdentityHub audience '{audience}' is already declared.");
        }

        return this;
    }

    public IIdentityHubApplicationBuilder AddClient(
        string clientId,
        Action<IdentityHubClientOptions> configure)
    {
        EnsureNotBuilt();
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(configure);
        if (_clients.ContainsKey(clientId))
        {
            throw new InvalidOperationException($"IdentityHub client '{clientId}' is already registered.");
        }

        var options = new IdentityHubClientOptions();
        configure.Invoke(options);
        _clients.Add(clientId, IdentityHubClientRegistration.Create(clientId, options));
        return this;
    }

    public IIdentityHubApplicationBuilder AddService(IHostService service)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);
        return this;
    }

    public IIdentityHubApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(factory);

        _serviceRegistrations.Add(factory);
        return this;
    }

    public IIdentityHubApplication Build()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException("The identity hub application has already been built.");
        }

        ValidateRegistrations();
        string environmentName = _resourceContext.EnvironmentName;
        var options = new IdentityHubApplicationOptions { Environment = environmentName };
        var context = new IdentityHubApplicationContext(environmentName);
        var hostedServices = new IHostService[_serviceRegistrations.Count + 1];

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[index] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The identity hub application service factory returned null.");
        }

        Uri endpoint = ResolveEndpoint();
        string dataPath = ResolveDataPath();
        Directory.CreateDirectory(dataPath);
        hostedServices[^1] = new IdentityEndpointService(
            endpoint,
            dataPath,
            _audiences,
            _clients,
            _controlPlane,
            _resourceContext,
            context);
        context.SetHostedServices(hostedServices);

        _isBuilt = true;
        var application = new IdentityHubApplicationHost(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }

        return application;
    }

    IHost IHostBuilder.Build() => Build();

    private void EnsureNotBuilt()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException("The identity hub application has already been built.");
        }
    }

    private void ValidateRegistrations()
    {
        foreach (IdentityHubClientRegistration client in _clients.Values)
        {
            if (!client.AllowsClientCredentials && !client.AllowDeviceAuthorization)
            {
                throw new InvalidOperationException(
                    $"IdentityHub client '{client.ClientId}' must enable client credentials or device authorization.");
            }

            if (client.Audiences.Count is 0)
            {
                throw new InvalidOperationException(
                    $"IdentityHub client '{client.ClientId}' must allow at least one declared audience.");
            }

            for (int index = 0; index < client.Audiences.Count; index++)
            {
                string audience = client.Audiences[index];
                if (!_audiences.Contains(audience))
                {
                    throw new InvalidOperationException(
                        $"IdentityHub client '{client.ClientId}' refers to undeclared audience '{audience}'.");
                }
            }
        }
    }

    private Uri ResolveEndpoint()
    {
        if (_controlPlane?.ObservedEndpoints.TryGetValue("https", out Uri? observed) is true)
        {
            return observed;
        }

        if (_resourceContext.Endpoints.TryGetValue("https", out Uri? supplied))
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
                    "The IdentityHub 'data' volume must expose a file-system path.");
            }

            return Path.GetFullPath(mount.Path);
        }

        string? configured = ParseArgument("--data");
        return Path.GetFullPath(configured ?? Path.Combine(_resourceContext.ContentRootPath, "data"));
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
                    throw new ArgumentException($"Command-line option '{name}' requires a value.", name);
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
            throw new ArgumentException(
                "The IdentityHub endpoint must be an absolute HTTP or HTTPS endpoint with an explicit port.",
                nameof(value));
        }

        return endpoint;
    }
}
