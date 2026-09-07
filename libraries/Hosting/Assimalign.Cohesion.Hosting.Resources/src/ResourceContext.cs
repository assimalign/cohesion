using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Carries one resource invocation's identity, endpoints, mounts, settings, references,
/// environment, and bootstrap credential.
/// </summary>
/// <remarks>
/// Out-of-process resources create this context from the frozen
/// <see cref="ResourceEnvironment"/> contract. In-process gateways supply the same values
/// directly and install the context with
/// <see cref="ResourceRuntime.CreateScope(ResourceContext)"/>.
/// </remarks>
public sealed class ResourceContext
{
    private const string EndpointToken = "<EP>";
    private const string MountToken = "<M>";
    private const string ConfigurationToken = "<Section>";

    private static readonly string EndpointPrefix = PatternPrefix(
        ResourceEnvironment.EndpointHostPattern,
        EndpointToken);
    private static readonly string EndpointHostSuffix = PatternSuffix(
        ResourceEnvironment.EndpointHostPattern,
        EndpointToken);
    private static readonly string MountPrefix = PatternPrefix(
        ResourceEnvironment.MountPathPattern,
        MountToken);
    private static readonly string MountSuffix = PatternSuffix(
        ResourceEnvironment.MountPathPattern,
        MountToken);
    private static readonly string ConfigurationPrefix = PatternPrefix(
        ResourceEnvironment.ConfigurationPattern,
        ConfigurationToken);

    private readonly Dictionary<string, string?> _environmentVariables;
    private readonly Dictionary<string, Uri> _endpoints;
    private readonly Dictionary<string, ResourceMount> _mounts;
    private readonly Dictionary<string, string> _settings;
    private readonly Dictionary<string, Uri> _references;
    private Func<string, object?>? _connectionFactoryResolver;

    /// <summary>
    /// Initializes an in-process resource context.
    /// </summary>
    /// <param name="applicationName">The application name.</param>
    /// <param name="resourceName">The resource name.</param>
    /// <param name="environmentName">The application environment name.</param>
    /// <param name="gatewayName">The gateway topology name, or null for standalone execution.</param>
    /// <param name="contentRootPath">The absolute content root, or null for the application base directory.</param>
    /// <param name="endpoints">Realized resource endpoints keyed by endpoint name.</param>
    /// <param name="mounts">Materialized resource mounts keyed by mount name.</param>
    /// <param name="settings">Configuration settings keyed by colon-separated setting name.</param>
    /// <param name="references">
    /// Observed dependency endpoints keyed as <c>&lt;resource&gt;:&lt;endpoint&gt;</c>.
    /// </param>
    /// <param name="bootstrapCredential">The bootstrap credential bytes for this invocation.</param>
    /// <exception cref="ArgumentNullException">
    /// A value in <paramref name="endpoints"/> or <paramref name="references"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="environmentName"/> is empty, <paramref name="contentRootPath"/> is not absolute,
    /// a supplied dictionary contains an empty or duplicate key, or a value in <paramref name="endpoints"/>
    /// or <paramref name="references"/> is not an endpoint URI.
    /// </exception>
    public ResourceContext(
        string? applicationName = null,
        string? resourceName = null,
        string? environmentName = null,
        string? gatewayName = null,
        string? contentRootPath = null,
        IReadOnlyDictionary<string, Uri>? endpoints = null,
        IReadOnlyDictionary<string, ResourceMount>? mounts = null,
        IReadOnlyDictionary<string, string>? settings = null,
        IReadOnlyDictionary<string, Uri>? references = null,
        ReadOnlyMemory<byte> bootstrapCredential = default)
        : this(
            applicationName,
            resourceName,
            environmentName ?? Assimalign.Cohesion.AppEnvironment.GetEnvironmentName(),
            gatewayName,
            contentRootPath,
            endpoints,
            mounts,
            settings,
            references,
            bootstrapCredential,
            new Dictionary<string, string?>(StringComparer.Ordinal))
    {
    }

    private ResourceContext(
        string? applicationName,
        string? resourceName,
        string environmentName,
        string? gatewayName,
        string? contentRootPath,
        IReadOnlyDictionary<string, Uri>? endpoints,
        IReadOnlyDictionary<string, ResourceMount>? mounts,
        IReadOnlyDictionary<string, string>? settings,
        IReadOnlyDictionary<string, Uri>? references,
        ReadOnlyMemory<byte> bootstrapCredential,
        Dictionary<string, string?> environmentVariables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        string resolvedContentRoot = string.IsNullOrWhiteSpace(contentRootPath)
            ? AppContext.BaseDirectory
            : contentRootPath;
        if (!Path.IsPathFullyQualified(resolvedContentRoot))
        {
            throw new ArgumentException(
                $"The resource content root must be an absolute path: '{resolvedContentRoot}'.",
                nameof(contentRootPath));
        }

        ApplicationName = NullIfWhiteSpace(applicationName);
        ResourceName = NullIfWhiteSpace(resourceName);
        EnvironmentName = environmentName;
        GatewayName = NullIfWhiteSpace(gatewayName);
        ContentRootPath = Path.GetFullPath(resolvedContentRoot);
        BootstrapCredential = bootstrapCredential.ToArray();
        _environmentVariables = environmentVariables;
        _endpoints = CopyEndpoints(endpoints, nameof(endpoints));
        _mounts = Copy(mounts, StringComparer.OrdinalIgnoreCase);
        _settings = Copy(settings, StringComparer.OrdinalIgnoreCase);
        _references = CopyEndpoints(references, nameof(references));
    }

    /// <summary>Gets the application name, when supplied by a gateway.</summary>
    public string? ApplicationName { get; }

    /// <summary>Gets the resource name, when supplied by a gateway.</summary>
    public string? ResourceName { get; }

    /// <summary>Gets the application environment name.</summary>
    public string EnvironmentName { get; }

    /// <summary>Gets the gateway topology name, or null for standalone execution.</summary>
    public string? GatewayName { get; }

    /// <summary>Gets the absolute resource content-root path.</summary>
    public string ContentRootPath { get; }

    /// <summary>Gets this resource invocation's bootstrap credential bytes.</summary>
    public ReadOnlyMemory<byte> BootstrapCredential { get; }

    /// <summary>Gets the endpoints supplied directly or discovered from the environment.</summary>
    public IReadOnlyDictionary<string, Uri> Endpoints =>
        new ReadOnlyDictionary<string, Uri>(_endpoints);

    /// <summary>Gets the mounts supplied directly or discovered from the environment.</summary>
    public IReadOnlyDictionary<string, ResourceMount> Mounts =>
        new ReadOnlyDictionary<string, ResourceMount>(_mounts);

    /// <summary>Gets the settings supplied directly or discovered from the environment.</summary>
    public IReadOnlyDictionary<string, string> Settings =>
        new ReadOnlyDictionary<string, string>(_settings);

    /// <summary>
    /// Gets directly supplied reference endpoints keyed as <c>&lt;resource&gt;:&lt;endpoint&gt;</c>.
    /// Environment-backed references remain available through <see cref="GetReference"/> and
    /// <see cref="TryGetReference"/> without guessing how normalized resource names should split.
    /// </summary>
    public IReadOnlyDictionary<string, Uri> References =>
        new ReadOnlyDictionary<string, Uri>(_references);

    /// <summary>Creates a resource context from the current process environment.</summary>
    /// <returns>A snapshot of the frozen resource environment contract.</returns>
    public static ResourceContext FromEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry variable in System.Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process))
        {
            if (variable.Key is string key)
            {
                environment[key] = variable.Value as string;
            }
        }

        return FromEnvironment(environment);
    }

    /// <summary>Creates a resource context from an environment-variable dictionary.</summary>
    /// <param name="environment">The environment-variable values to snapshot.</param>
    /// <returns>A context populated from the frozen resource environment contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    public static ResourceContext FromEnvironment(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var snapshot = new Dictionary<string, string?>(environment, StringComparer.Ordinal);
        Dictionary<string, Uri> endpoints = ReadEndpoints(snapshot);
        Dictionary<string, ResourceMount> mounts = ReadMounts(snapshot);
        Dictionary<string, string> settings = ReadSettings(snapshot);
        ReadOnlyMemory<byte> bootstrapCredential = ReadBootstrapCredential(snapshot);

        return new ResourceContext(
            ResourceEnvironment.GetValue(snapshot, ResourceEnvironment.Application),
            ResourceEnvironment.GetValue(snapshot, ResourceEnvironment.Resource),
            Assimalign.Cohesion.AppEnvironment.GetEnvironmentName(snapshot),
            ResourceEnvironment.GetValue(snapshot, ResourceEnvironment.Gateway),
            ResourceEnvironment.GetValue(snapshot, ResourceEnvironment.ContentRoot),
            endpoints,
            mounts,
            settings,
            references: null,
            bootstrapCredential,
            snapshot);
    }

    /// <summary>Resolves a realized endpoint or its standalone development fallback.</summary>
    /// <param name="name">The endpoint name.</param>
    /// <param name="scheme">The declared endpoint scheme.</param>
    /// <param name="devPort">The development port used only when no gateway is present.</param>
    /// <returns>The realized endpoint address.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="scheme"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> or <paramref name="scheme"/> is empty, or <paramref name="scheme"/> is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="devPort"/> is outside the range 1 through 65535 when a standalone fallback is required.
    /// </exception>
    /// <exception cref="InvalidOperationException">The endpoint is not bound and has no permitted fallback.</exception>
    public Uri GetEndpoint(string name, string scheme, int? devPort)
    {
        if (TryGetEndpoint(name, scheme, devPort, out Uri? address))
        {
            return address;
        }

        throw new InvalidOperationException(
            $"Cohesion endpoint '{name}' is not bound and has no DevPort for standalone execution.");
    }

    /// <summary>
    /// Attempts to resolve a realized endpoint or its standalone development fallback.
    /// </summary>
    /// <param name="name">The endpoint name.</param>
    /// <param name="scheme">The declared endpoint scheme.</param>
    /// <param name="devPort">The development port used only when no gateway is present.</param>
    /// <param name="address">The realized endpoint when this method returns true.</param>
    /// <returns>True when the endpoint is bound or has a permitted fallback; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="scheme"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> or <paramref name="scheme"/> is empty, or <paramref name="scheme"/> is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="devPort"/> is outside the range 1 through 65535 when a standalone fallback is required.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A gateway supplied an incomplete endpoint binding.
    /// </exception>
    public bool TryGetEndpoint(
        string name,
        string scheme,
        int? devPort,
        [NotNullWhen(true)] out Uri? address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);

        if (_endpoints.TryGetValue(name, out address)
            || ResourceEnvironment.TryGetEndpoint(_environmentVariables, name, out address))
        {
            return true;
        }

        if (GatewayName is null)
        {
            if (devPort is int standalonePort)
            {
                address = Uri.CreateEndpoint(scheme, "localhost", standalonePort);
                return true;
            }

            address = null;
            return false;
        }

        throw new InvalidOperationException(
            $"Cohesion endpoint '{name}' is not completely bound by gateway '{GatewayName}'.");
    }

    /// <summary>Resolves a materialized mount.</summary>
    /// <param name="name">The mount name.</param>
    /// <param name="fallbackPath">The manifest-declared path used outside a gateway.</param>
    /// <returns>The resource mount reader.</returns>
    public ResourceMount GetMount(string name, string fallbackPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackPath);

        if (_mounts.TryGetValue(name, out ResourceMount? mount))
        {
            return mount;
        }

        if (ResourceEnvironment.TryGetMount(_environmentVariables, name, out string? path))
        {
            return new ResourceMount(path);
        }

        return new ResourceMount(fallbackPath);
    }

    /// <summary>Resolves a configured setting or its manifest default.</summary>
    /// <param name="key">The colon-separated setting key.</param>
    /// <param name="fallback">The manifest default.</param>
    /// <returns>The configured setting value.</returns>
    /// <exception cref="InvalidOperationException">A required setting has no value.</exception>
    public string GetSetting(string key, string? fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_settings.TryGetValue(key, out string? value))
        {
            return value;
        }

        int separator = key.IndexOf(':');
        string section = separator < 0 ? "Default" : key[..separator];
        string setting = separator < 0
            ? key
            : key[(separator + 1)..].Replace(":", "__", StringComparison.Ordinal);
        string variable = ResourceEnvironment.Configuration(section, setting);

        return ResourceEnvironment.GetValue(_environmentVariables, variable)
            ?? fallback
            ?? throw new InvalidOperationException($"Required Cohesion setting '{key}' is not configured.");
    }

    /// <summary>Gets an observed endpoint on a referenced resource.</summary>
    /// <param name="resource">The referenced resource name.</param>
    /// <param name="endpoint">The referenced endpoint name.</param>
    /// <returns>The observed endpoint.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resource"/> or <paramref name="endpoint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="resource"/> or <paramref name="endpoint"/> is empty.
    /// </exception>
    /// <exception cref="InvalidOperationException">The reference is not resolved.</exception>
    public Uri GetReference(string resource, string endpoint)
    {
        return TryGetReference(resource, endpoint, out Uri? address)
            ? address
            : throw new InvalidOperationException(
                $"Cohesion reference '{resource}:{endpoint}' is not currently resolved.");
    }

    /// <summary>Attempts to get an observed endpoint on a referenced resource.</summary>
    /// <param name="resource">The referenced resource name.</param>
    /// <param name="endpoint">The referenced endpoint name.</param>
    /// <param name="address">The observed endpoint when resolved.</param>
    /// <returns>True when the reference is resolved; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resource"/> or <paramref name="endpoint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="resource"/> or <paramref name="endpoint"/> is empty.
    /// </exception>
    public bool TryGetReference(
        string resource,
        string endpoint,
        [NotNullWhen(true)] out Uri? address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        return _references.TryGetValue(ReferenceKey(resource, endpoint), out address)
            || ResourceEnvironment.TryGetDependency(_environmentVariables, resource, endpoint, out address);
    }

    /// <summary>Creates a connection factory for an observed reference's declared protocol.</summary>
    /// <typeparam name="TConnectionFactory">The connection-factory contract exposed by the resource SDK.</typeparam>
    /// <param name="resource">The referenced resource name.</param>
    /// <param name="endpoint">The referenced endpoint name.</param>
    /// <param name="protocol">The endpoint's declared transport protocol.</param>
    /// <returns>A connection factory supplied by the current area runtime.</returns>
    /// <exception cref="InvalidOperationException">
    /// The area builder has not installed a connection-factory resolver, or the resolver returned
    /// an incompatible object.
    /// </exception>
    /// <exception cref="NotSupportedException">The area runtime does not support the protocol.</exception>
    public TConnectionFactory GetConnectionFactory<TConnectionFactory>(
        string resource,
        string endpoint,
        string protocol)
        where TConnectionFactory : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);

        Func<string, object?> resolver = Volatile.Read(ref _connectionFactoryResolver)
            ?? throw new InvalidOperationException(
                "The current resource area has not installed its connection-factory resolver. " +
                "Create the area application builder before resolving a referenced connection factory.");
        object factory = resolver.Invoke(protocol)
            ?? throw new NotSupportedException(
                $"Cohesion reference '{resource}:{endpoint}' uses '{protocol}', which does not expose a stream connection factory.");

        return factory as TConnectionFactory
            ?? throw new InvalidOperationException(
                $"The '{protocol}' resolver returned '{factory.GetType().FullName}', not '{typeof(TConnectionFactory).FullName}'.");
    }

    internal string? GetEnvironmentValue(string name)
        => ResourceEnvironment.GetValue(_environmentVariables, name);

    internal void SetConnectionFactoryResolver(Func<string, object?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        Volatile.Write(ref _connectionFactoryResolver, resolver);
    }

    private static Dictionary<string, Uri> ReadEndpoints(IDictionary<string, string?> environment)
    {
        var endpoints = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        foreach (string variable in environment.Keys)
        {
            if (!variable.StartsWith(EndpointPrefix, StringComparison.Ordinal)
                || !variable.EndsWith(EndpointHostSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            string name = variable[EndpointPrefix.Length..^EndpointHostSuffix.Length];
            if (ResourceEnvironment.TryGetEndpoint(environment, name, out Uri? endpoint))
            {
                endpoints[name] = endpoint;
            }
        }

        return endpoints;
    }

    private static Dictionary<string, ResourceMount> ReadMounts(IDictionary<string, string?> environment)
    {
        var mounts = new Dictionary<string, ResourceMount>(StringComparer.OrdinalIgnoreCase);
        foreach ((string variable, string? value) in environment)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !variable.StartsWith(MountPrefix, StringComparison.Ordinal)
                || !variable.EndsWith(MountSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            string name = variable[MountPrefix.Length..^MountSuffix.Length];
            mounts[name] = new ResourceMount(value);
        }

        return mounts;
    }

    private static Dictionary<string, string> ReadSettings(IDictionary<string, string?> environment)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string variable, string? value) in environment)
        {
            if (value is null || !variable.StartsWith(ConfigurationPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string key = variable[ConfigurationPrefix.Length..]
                .Replace("__", ":", StringComparison.Ordinal);
            settings[key] = value;
        }

        return settings;
    }

    private static ReadOnlyMemory<byte> ReadBootstrapCredential(IDictionary<string, string?> environment)
    {
        string? path = ResourceEnvironment.GetValue(environment, ResourceEnvironment.BootstrapTokenPath);
        return string.IsNullOrWhiteSpace(path)
            ? ReadOnlyMemory<byte>.Empty
            : new ResourceMount(path).ReadAllBytes();
    }

    private static Dictionary<string, TValue> Copy<TValue>(
        IReadOnlyDictionary<string, TValue>? source,
        StringComparer comparer)
    {
        var result = new Dictionary<string, TValue>(comparer);
        if (source is null)
        {
            return result;
        }

        foreach ((string key, TValue value) in source)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            result.Add(key, value);
        }

        return result;
    }

    private static Dictionary<string, Uri> CopyEndpoints(
        IReadOnlyDictionary<string, Uri>? source,
        string paramName)
    {
        var result = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }

        foreach ((string key, Uri value) in source)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            result.Add(key, Uri.ThrowIfNotEndpoint(value, $"{paramName}[{key}]"));
        }

        return result;
    }

    private static string ReferenceKey(string resource, string endpoint) => $"{resource}:{endpoint}";

    private static string PatternPrefix(string pattern, string token)
    {
        int tokenIndex = pattern.IndexOf(token, StringComparison.Ordinal);
        return tokenIndex < 0
            ? throw new InvalidOperationException($"Resource environment pattern '{pattern}' has no '{token}' token.")
            : pattern[..tokenIndex];
    }

    private static string PatternSuffix(string pattern, string token)
    {
        int tokenIndex = pattern.IndexOf(token, StringComparison.Ordinal);
        return tokenIndex < 0
            ? throw new InvalidOperationException($"Resource environment pattern '{pattern}' has no '{token}' token.")
            : pattern[(tokenIndex + token.Length)..];
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
