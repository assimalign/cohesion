using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

internal sealed class ConfigurationStoreApplicationBuilder : IConfigurationStoreApplicationBuilder
{
    private const string DefaultEndpoint = "http://127.0.0.1:8080";

    private readonly string[] _args;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string?>> _namespaces =
        new(StringComparer.Ordinal);
    private readonly ResourceContext? _resourceContext;
    private readonly List<Func<IHostContext, IHostService>> _serviceRegistrations = new();
    private bool _isBuilt;

    internal ConfigurationStoreApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        _args = (string[])args.Clone();
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException(
                "The registered ConfigurationStore control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
            _loggerFactory = ResourceTelemetry.Configure(_resourceContext, out IHostService? telemetry);
            if (telemetry is not null)
            {
                _serviceRegistrations.Insert(0, _ => telemetry);
            }
        }
    }

    public IConfigurationStoreApplicationBuilder AddNamespace(
        string name,
        Action<IConfigurationNamespaceBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        if (_namespaces.ContainsKey(name))
        {
            throw new InvalidOperationException(
                $"Configuration namespace '{name}' is already declared.");
        }

        var declaration = new ConfigurationNamespaceBuilder();
        configure.Invoke(declaration);
        _namespaces.Add(name, declaration.Snapshot());
        return this;
    }

    public IConfigurationStoreApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);
        return this;
    }

    public IConfigurationStoreApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceRegistrations.Add(factory);
        return this;
    }

    public IConfigurationStoreApplication Build()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException("The configuration store application has already been built.");
        }

        string environmentName = _resourceContext?.EnvironmentName ?? "production";
        var options = new ConfigurationStoreApplicationOptions { Environment = environmentName };
        var context = new ConfigurationStoreApplicationContext(
            environmentName,
            _resourceContext is null ? null : System.IO.FileSystemPath.Parse(_resourceContext.ContentRootPath));
        var hostedServices = new IHostService[_serviceRegistrations.Count + 1];

        for (int index = 0; index < _serviceRegistrations.Count; index++)
        {
            hostedServices[index] = _serviceRegistrations[index].Invoke(context)
                ?? throw new InvalidOperationException(
                    "The configuration store application service factory returned null.");
        }

        Uri endpoint = ResolveEndpoint();
        string dataPath = ResolveDataPath();
        var repository = new ConfigurationStoreRepository(dataPath, _namespaces);
        hostedServices[^1] = new ConfigurationEndpointService(
            endpoint,
            repository,
            _controlPlane,
            _resourceContext,
            context);

        context.SetHostedServices(hostedServices);

        _isBuilt = true;
        var application = new ConfigurationStoreApplicationHost(options, context);
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

        if (_resourceContext?.Endpoints.TryGetValue("api", out Uri? supplied) is true)
        {
            return supplied;
        }

        return ParseArgument("--endpoint") is string configured
            ? ParseEndpoint(configured)
            : new Uri(DefaultEndpoint, UriKind.Absolute);
    }

    private string ResolveDataPath()
    {
        if (_resourceContext?.Mounts.TryGetValue("data", out ResourceMount? mount) is true)
        {
            if (string.IsNullOrWhiteSpace(mount.Path))
            {
                throw new InvalidOperationException(
                    "The ConfigurationStore 'data' volume must expose a file-system path.");
            }

            return Path.GetFullPath(mount.Path);
        }

        string? configured = ParseArgument("--data");
        string path = configured ?? Path.Combine(
            _resourceContext?.ContentRootPath ?? AppContext.BaseDirectory,
            "data");
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
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(endpoint.Host) ||
            endpoint.Port is < 1 or > 65535 ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "The ConfigurationStore endpoint must be an absolute HTTP endpoint with an explicit port.",
                nameof(value));
        }

        return endpoint;
    }
}
