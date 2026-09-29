using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Assimalign.Cohesion;

/// <summary>
/// Reads the Cohesion runtime contract from the process environment: the application environment
/// name, raw variable values, and the typed endpoint, dependency, and mount readers over the names
/// that <see cref="Variables"/> defines.
/// </summary>
public static partial class AppEnvironment
{
    /// <summary>
    /// Gets the environment name for the current process.
    /// </summary>
    /// <returns>
    /// The Cohesion environment override, the .NET environment fallback, or
    /// <see cref="Keys.DefaultEnvironmentName"/> when neither variable is set.
    /// </returns>
    public static string GetEnvironmentName()
    {
        return GetEnvironmentNameCore(environment: null);
    }

    /// <summary>
    /// Gets the environment name from an environment-variable dictionary.
    /// </summary>
    /// <param name="environment">The environment-variable values to read.</param>
    /// <returns>
    /// The Cohesion environment override, the .NET environment fallback, or
    /// <see cref="Keys.DefaultEnvironmentName"/> when neither variable is present.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="environment"/> is <see langword="null"/>.
    /// </exception>
    public static string GetEnvironmentName(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return GetEnvironmentNameCore(environment);
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
        path = GetValue(Variables.Mount(mount));
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
        path = GetValue(environment, Variables.Mount(mount));
        return !string.IsNullOrWhiteSpace(path);
    }

    private static string GetEnvironmentNameCore(IDictionary<string, string?>? environment)
    {
        string? cohesionEnvironment = ReadValue(environment, Keys.EnvironmentKey);
        string? dotNetEnvironment = ReadValue(environment, Keys.DotNetEnvironmentKey);

        return cohesionEnvironment ?? dotNetEnvironment ?? Keys.DefaultEnvironmentName;
    }

    private static bool TryGetEndpointCore(
        IDictionary<string, string?>? environment,
        string endpoint,
        [NotNullWhen(true)] out Uri? address)
    {
        string hostVariable = Variables.Endpoint(endpoint, "HOST");
        string portVariable = Variables.Endpoint(endpoint, "PORT");
        string schemeVariable = Variables.Endpoint(endpoint, "SCHEME");
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
        string? url = ReadValue(environment, Variables.Dependency(resource, endpoint, "URL"));
        if (!string.IsNullOrWhiteSpace(url))
        {
            return Uri.TryParseEndpoint(url, out address);
        }

        return TryCreateAddress(
            ReadValue(environment, Variables.Dependency(resource, endpoint, "SCHEME")),
            ReadValue(environment, Variables.Dependency(resource, endpoint, "HOST")),
            ReadValue(environment, Variables.Dependency(resource, endpoint, "PORT")),
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
}
