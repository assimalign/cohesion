using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.IdentityHub;
using Assimalign.Cohesion.IdentityHub.Hosting.Internal;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

/// <summary>
/// Composes an IdentityHub application and its hosting services.
/// </summary>
public sealed class IdentityHubApplicationBuilder : IIdentityHubApplicationBuilder
{
    private const string DefaultEndpoint = "https://127.0.0.1:8443";

    private readonly string[] _args;
    private readonly Dictionary<string, IdentityHubClientRegistration> _clients = new(StringComparer.Ordinal);
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly HashSet<string> _audiences = new(StringComparer.Ordinal);
    private readonly ResourceContext _resourceContext;
    private readonly List<Func<IdentityHubApplicationContext, IHostService>> _serviceRegistrations = new();
    private bool _isBuilt;

    internal IdentityHubApplicationBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        _args = (string[])args.Clone();
        _resourceContext = ResourceRuntime.Current;
        _loggerFactory = ResourceTelemetry.Configure(_resourceContext, out IHostService? telemetry);
        if (telemetry is not null)
        {
            _serviceRegistrations.Insert(0, _ => telemetry);
        }
        if (ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException(
                "The registered IdentityHub control-plane factory returned null.");
        }
    }

    /// <summary>
    /// Declares an audience for access tokens issued by this identity hub.
    /// </summary>
    /// <param name="audience">The exact audience identifier.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="audience"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The audience is already declared or the application has been built.</exception>
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

    /// <summary>
    /// Registers an OAuth client in the identity hub's code-first configuration.
    /// </summary>
    /// <param name="clientId">The exact client identifier.</param>
    /// <param name="configure">The callback that configures grants, credentials, and audiences.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="clientId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The client is already registered or the application has been built.</exception>
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

    /// <summary>
    /// Registers a host service with the identity hub application.
    /// </summary>
    /// <remarks>
    /// Host services start in registration order and stop in reverse registration order.
    /// </remarks>
    /// <param name="service">The host service to register.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The application has been built.</exception>
    public IdentityHubApplicationBuilder AddService(IHostService service)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(service);

        _serviceRegistrations.Add(_ => service);
        return this;
    }

    /// <summary>
    /// Registers a host service factory with the identity hub application.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once for each call to <see cref="Build"/> and receives that
    /// application's final host context. The resulting service follows registration order.
    /// </remarks>
    /// <param name="factory">The factory that creates the host service.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The application has been built, or the factory returns <see langword="null"/> during build.</exception>
    public IdentityHubApplicationBuilder AddService(Func<IdentityHubApplicationContext, IHostService> factory)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(factory);

        _serviceRegistrations.Add(factory);
        return this;
    }

    /// <summary>
    /// Builds the identity hub application.
    /// </summary>
    /// <returns>The configured identity hub application.</returns>
    /// <exception cref="InvalidOperationException">
    /// The application has already been built, a client has no valid grant or declared audience,
    /// a service factory returns null, or the data mount has no filesystem path.
    /// </exception>
    /// <exception cref="ArgumentException">A command-line endpoint or data option is invalid or missing its value.</exception>
    public IdentityHubApplication Build()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException("The identity hub application has already been built.");
        }

        ValidateRegistrations();
        string environmentName = _resourceContext.EnvironmentName;
        var options = new IdentityHubApplicationOptions { Environment = environmentName };
        var context = new IdentityHubApplicationContext(
            environmentName,
            _resourceContext is null ? null : System.IO.FileSystemPath.Parse(_resourceContext.ContentRootPath));
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
        var application = new IdentityHubApplication(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }

        return application;
    }

    IIdentityHubApplication IIdentityHubApplicationBuilder.Build() => Build();

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
