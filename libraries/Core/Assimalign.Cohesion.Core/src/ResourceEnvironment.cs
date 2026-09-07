using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Assimalign.Cohesion.Core;

/// <summary>
/// Defines the frozen version 1 environment-variable contract shared by Cohesion gateways and resources.
/// </summary>
public static class ResourceEnvironment
{
    /// <summary>Gets the application identity variable.</summary>
    public const string Application = "COHESION_APPLICATION";

    /// <summary>Gets the resource identity variable.</summary>
    public const string Resource = "COHESION_RESOURCE";

    /// <summary>Gets the gateway topology variable.</summary>
    public const string Gateway = "COHESION_GATEWAY";

    /// <summary>Gets the application environment-name variable.</summary>
    public const string Environment = "COHESION_ENVIRONMENT";

    /// <summary>Gets the resource content-root variable.</summary>
    public const string ContentRoot = "COHESION_CONTENT_ROOT";

    /// <summary>Gets the application public trust-key variable.</summary>
    public const string ApplicationTrustKey = "COHESION_APPLICATION_TRUST_KEY";

    /// <summary>Gets the declared endpoint host variable pattern.</summary>
    public const string EndpointHostPattern = "COHESION_ENDPOINT_<EP>_HOST";

    /// <summary>Gets the declared endpoint port variable pattern.</summary>
    public const string EndpointPortPattern = "COHESION_ENDPOINT_<EP>_PORT";

    /// <summary>Gets the declared endpoint scheme variable pattern.</summary>
    public const string EndpointSchemePattern = "COHESION_ENDPOINT_<EP>_SCHEME";

    /// <summary>Gets the declared endpoint public URL variable pattern.</summary>
    public const string EndpointPublicUrlPattern = "COHESION_ENDPOINT_<EP>_PUBLIC_URL";

    /// <summary>Gets the dependency URL variable pattern.</summary>
    public const string DependencyUrlPattern = "COHESION_DEPENDENCY_<RES>_<EP>_URL";

    /// <summary>Gets the dependency host variable pattern.</summary>
    public const string DependencyHostPattern = "COHESION_DEPENDENCY_<RES>_<EP>_HOST";

    /// <summary>Gets the dependency port variable pattern.</summary>
    public const string DependencyPortPattern = "COHESION_DEPENDENCY_<RES>_<EP>_PORT";

    /// <summary>Gets the dependency scheme variable pattern.</summary>
    public const string DependencySchemePattern = "COHESION_DEPENDENCY_<RES>_<EP>_SCHEME";

    /// <summary>Gets the resource mount path variable pattern.</summary>
    public const string MountPathPattern = "COHESION_MOUNT_<M>_PATH";

    /// <summary>Gets the configuration bridge variable pattern.</summary>
    public const string ConfigurationPattern = "COHESION_CONFIG__<Section>__<Key>";

    /// <summary>Gets the bootstrap credential file-path variable.</summary>
    public const string BootstrapTokenPath = "COHESION_BOOTSTRAP_TOKEN_PATH";

    /// <summary>Gets the Windows graceful-stop event variable.</summary>
    public const string StopEvent = "COHESION_STOP_EVENT";

    /// <summary>Gets the optional OpenTelemetry collector endpoint variable.</summary>
    public const string TelemetryEndpoint = "COHESION_TELEMETRY_ENDPOINT";

    /// <summary>Gets the optional OpenTelemetry transport protocol variable.</summary>
    public const string TelemetryProtocol = "COHESION_TELEMETRY_PROTOCOL";

    /// <summary>Gets the optional OpenTelemetry headers file-path variable.</summary>
    public const string TelemetryHeadersPath = "COHESION_TELEMETRY_HEADERS_PATH";

    /// <summary>Gets the optional structured log format variable.</summary>
    public const string LogFormat = "COHESION_LOG_FORMAT";

    /// <summary>
    /// Builds a declared-endpoint environment-variable name.
    /// </summary>
    /// <param name="endpoint">The declared endpoint name.</param>
    /// <param name="suffix"><c>HOST</c>, <c>PORT</c>, <c>SCHEME</c>, or <c>PUBLIC_URL</c>.</param>
    /// <returns>The canonical endpoint variable name.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="endpoint"/> or <paramref name="suffix"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="suffix"/> is not a supported endpoint suffix.
    /// </exception>
    public static string Endpoint(string endpoint, string suffix)
    {
        string normalizedEndpoint = NormalizeName(endpoint);
        string normalizedSuffix = NormalizeName(suffix);
        string pattern = normalizedSuffix switch
        {
            "HOST" => EndpointHostPattern,
            "PORT" => EndpointPortPattern,
            "SCHEME" => EndpointSchemePattern,
            "PUBLIC_URL" => EndpointPublicUrlPattern,
            _ => throw new ArgumentOutOfRangeException(
                nameof(suffix),
                suffix,
                "The endpoint suffix must be HOST, PORT, SCHEME, or PUBLIC_URL.")
        };

        return pattern.Replace("<EP>", normalizedEndpoint, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a dependency endpoint environment-variable name.
    /// </summary>
    /// <param name="resource">The dependency resource name.</param>
    /// <param name="endpoint">The dependency endpoint name.</param>
    /// <param name="suffix"><c>URL</c>, <c>HOST</c>, <c>PORT</c>, or <c>SCHEME</c>.</param>
    /// <returns>The canonical dependency variable name.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="resource"/>, <paramref name="endpoint"/>, or
    /// <paramref name="suffix"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="suffix"/> is not a supported dependency suffix.
    /// </exception>
    public static string Dependency(string resource, string endpoint, string suffix)
    {
        string normalizedResource = NormalizeName(resource);
        string normalizedEndpoint = NormalizeName(endpoint);
        string normalizedSuffix = NormalizeName(suffix);
        string pattern = normalizedSuffix switch
        {
            "URL" => DependencyUrlPattern,
            "HOST" => DependencyHostPattern,
            "PORT" => DependencyPortPattern,
            "SCHEME" => DependencySchemePattern,
            _ => throw new ArgumentOutOfRangeException(
                nameof(suffix),
                suffix,
                "The dependency suffix must be URL, HOST, PORT, or SCHEME.")
        };

        return pattern
            .Replace("<RES>", normalizedResource, StringComparison.Ordinal)
            .Replace("<EP>", normalizedEndpoint, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a resource mount path environment-variable name.
    /// </summary>
    /// <param name="mount">The declared mount name.</param>
    /// <returns>The canonical mount path variable name.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="mount"/> is empty.</exception>
    public static string Mount(string mount)
    {
        return MountPathPattern.Replace("<M>", NormalizeName(mount), StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a configuration bridge environment-variable name.
    /// </summary>
    /// <param name="section">The configuration section name.</param>
    /// <param name="key">The configuration key name.</param>
    /// <returns>The configuration variable name, using a double underscore as the section separator.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="section"/> or <paramref name="key"/> is empty.
    /// </exception>
    public static string Configuration(string section, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return ConfigurationPattern
            .Replace("<Section>", section, StringComparison.Ordinal)
            .Replace("<Key>", key, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets a variable from the current process environment.
    /// </summary>
    /// <param name="variable">The variable name.</param>
    /// <returns>The variable value, or <see langword="null"/> when it is not set.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="variable"/> is empty.</exception>
    public static string? GetValue(string variable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);

        return System.Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Process);
    }

    /// <summary>
    /// Gets a variable from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <param name="variable">The variable name.</param>
    /// <returns>The variable value, or <see langword="null"/> when it is not present.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="variable"/> is empty.</exception>
    public static string? GetValue(IDictionary<string, string?> environment, string variable)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);

        return environment.TryGetValue(variable, out string? value) ? value : null;
    }

    /// <summary>
    /// Attempts to read a port from the current process environment.
    /// </summary>
    /// <param name="variable">The port variable name.</param>
    /// <param name="port">The parsed port when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a valid port was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="variable"/> is empty.</exception>
    public static bool TryGetPort(string variable, out int port)
    {
        return TryParsePort(GetValue(variable), out port);
    }

    /// <summary>
    /// Attempts to read a port from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <param name="variable">The port variable name.</param>
    /// <param name="port">The parsed port when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a valid port was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="variable"/> is empty.</exception>
    public static bool TryGetPort(IDictionary<string, string?> environment, string variable, out int port)
    {
        return TryParsePort(GetValue(environment, variable), out port);
    }

    /// <summary>
    /// Attempts to read an absolute URI from the current process environment.
    /// </summary>
    /// <param name="variable">The URI variable name.</param>
    /// <param name="uri">The parsed URI when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when an absolute URI was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="variable"/> is empty.</exception>
    public static bool TryGetUri(string variable, [NotNullWhen(true)] out Uri? uri)
    {
        return Uri.TryCreate(GetValue(variable), UriKind.Absolute, out uri);
    }

    /// <summary>
    /// Attempts to read an absolute URI from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <param name="variable">The URI variable name.</param>
    /// <param name="uri">The parsed URI when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when an absolute URI was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="variable"/> is empty.</exception>
    public static bool TryGetUri(
        IDictionary<string, string?> environment,
        string variable,
        [NotNullWhen(true)] out Uri? uri)
    {
        return Uri.TryCreate(GetValue(environment, variable), UriKind.Absolute, out uri);
    }

    /// <summary>
    /// Attempts to read a declared endpoint from the current process environment.
    /// </summary>
    /// <param name="endpoint">The declared endpoint name.</param>
    /// <param name="address">The endpoint address when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a complete endpoint address was read; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// This method reads the bind host, port, and scheme. Read the separately advertised public
    /// URL with <see cref="TryGetUri"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="endpoint"/> is empty.</exception>
    public static bool TryGetEndpoint(string endpoint, [NotNullWhen(true)] out Uri? address)
    {
        return TryGetEndpointCore(environment: null, endpoint, out address);
    }

    /// <summary>
    /// Attempts to read a declared endpoint from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <param name="endpoint">The declared endpoint name.</param>
    /// <param name="address">The endpoint address when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a complete endpoint address was read; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// This method reads the bind host, port, and scheme. Read the separately advertised public
    /// URL with <see cref="TryGetUri"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="endpoint"/> is empty.</exception>
    public static bool TryGetEndpoint(
        IDictionary<string, string?> environment,
        string endpoint,
        [NotNullWhen(true)] out Uri? address)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return TryGetEndpointCore(environment, endpoint, out address);
    }

    /// <summary>
    /// Attempts to read a dependency endpoint from the current process environment.
    /// </summary>
    /// <param name="resource">The dependency resource name.</param>
    /// <param name="endpoint">The dependency endpoint name.</param>
    /// <param name="address">The dependency address when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a dependency address was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="resource"/> or <paramref name="endpoint"/> is empty.
    /// </exception>
    public static bool TryGetDependency(
        string resource,
        string endpoint,
        [NotNullWhen(true)] out Uri? address)
    {
        return TryGetDependencyCore(environment: null, resource, endpoint, out address);
    }

    /// <summary>
    /// Attempts to read a dependency endpoint from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <param name="resource">The dependency resource name.</param>
    /// <param name="endpoint">The dependency endpoint name.</param>
    /// <param name="address">The dependency address when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a dependency address was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="resource"/> or <paramref name="endpoint"/> is empty.
    /// </exception>
    public static bool TryGetDependency(
        IDictionary<string, string?> environment,
        string resource,
        string endpoint,
        [NotNullWhen(true)] out Uri? address)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return TryGetDependencyCore(environment, resource, endpoint, out address);
    }

    /// <summary>
    /// Attempts to read a mount path from the current process environment.
    /// </summary>
    /// <param name="mount">The declared mount name.</param>
    /// <param name="path">The mount path when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a non-empty mount path was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="mount"/> is empty.</exception>
    public static bool TryGetMount(string mount, [NotNullWhen(true)] out string? path)
    {
        path = GetValue(Mount(mount));
        return !string.IsNullOrWhiteSpace(path);
    }

    /// <summary>
    /// Attempts to read a mount path from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <param name="mount">The declared mount name.</param>
    /// <param name="path">The mount path when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a non-empty mount path was read; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="mount"/> is empty.</exception>
    public static bool TryGetMount(
        IDictionary<string, string?> environment,
        string mount,
        [NotNullWhen(true)] out string? path)
    {
        path = GetValue(environment, Mount(mount));
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool TryGetEndpointCore(
        IDictionary<string, string?>? environment,
        string endpoint,
        [NotNullWhen(true)] out Uri? address)
    {
        string hostVariable = Endpoint(endpoint, "HOST");
        string portVariable = Endpoint(endpoint, "PORT");
        string schemeVariable = Endpoint(endpoint, "SCHEME");
        string? host = ReadValue(environment, hostVariable);
        string? port = ReadValue(environment, portVariable);
        string? scheme = ReadValue(environment, schemeVariable);

        return TryCreateAddress(scheme, host, port, out address);
    }

    private static bool TryGetDependencyCore(
        IDictionary<string, string?>? environment,
        string resource,
        string endpoint,
        [NotNullWhen(true)] out Uri? address)
    {
        string? url = ReadValue(environment, Dependency(resource, endpoint, "URL"));
        if (!string.IsNullOrWhiteSpace(url))
        {
            return Uri.TryParseEndpoint(url, out address);
        }

        return TryCreateAddress(
            ReadValue(environment, Dependency(resource, endpoint, "SCHEME")),
            ReadValue(environment, Dependency(resource, endpoint, "HOST")),
            ReadValue(environment, Dependency(resource, endpoint, "PORT")),
            out address);
    }

    private static bool TryCreateAddress(
        string? scheme,
        string? host,
        string? portValue,
        [NotNullWhen(true)] out Uri? address)
    {
        address = null;

        if (string.IsNullOrWhiteSpace(scheme)
            || string.IsNullOrWhiteSpace(host)
            || !TryParsePort(portValue, out int port))
        {
            return false;
        }

        return Uri.TryCreateEndpoint(scheme, host, port, path: null, out address);
    }

    private static string? ReadValue(IDictionary<string, string?>? environment, string variable)
    {
        return environment is null ? GetValue(variable) : GetValue(environment, variable);
    }

    private static bool TryParsePort(string? value, out int port)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535)
        {
            port = default;
            return false;
        }

        return true;
    }

    private static string NormalizeName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        char[] normalized = new char[value.Length];
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            normalized[index] = character switch
            {
                >= 'a' and <= 'z' => (char)(character - ('a' - 'A')),
                >= 'A' and <= 'Z' or >= '0' and <= '9' => character,
                _ => '_'
            };
        }

        return new string(normalized);
    }
}
