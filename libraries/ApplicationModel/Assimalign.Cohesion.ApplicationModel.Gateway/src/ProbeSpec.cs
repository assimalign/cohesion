using System;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Creates immutable <see cref="IProbeSpec"/> values for local process probes.
/// </summary>
public static class ProbeSpec
{
    /// <summary>Creates an HTTP probe against a named resource endpoint.</summary>
    /// <param name="endpoint">The logical endpoint name.</param>
    /// <param name="path">The absolute request path.</param>
    /// <returns>An HTTP probe specification.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="endpoint"/> or <paramref name="path"/> is empty, or
    /// <paramref name="path"/> is not absolute.
    /// </exception>
    public static IProbeSpec Http(string endpoint, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("An HTTP probe path must begin with '/'.", nameof(path));
        }

        return new ProbeDefinition(ProbeKind.Http, endpoint, address: null, path, Array.Empty<string>());
    }

    /// <summary>Creates an HTTP probe against an absolute address.</summary>
    /// <param name="address">The absolute HTTP or HTTPS address.</param>
    /// <returns>An HTTP probe specification.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="address"/> does not use the HTTP or HTTPS scheme.
    /// </exception>
    public static IProbeSpec Http(EndpointAddress address)
    {
        return Http(address.Url);
    }

    /// <summary>Creates an HTTP probe against an absolute address.</summary>
    /// <param name="address">The absolute HTTP or HTTPS endpoint URI.</param>
    /// <returns>An HTTP probe specification.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="address"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="address"/> is not an endpoint URI or does not use the HTTP or HTTPS scheme.
    /// </exception>
    public static IProbeSpec Http(Uri address)
    {
        Uri.ThrowIfNotEndpoint(address);

        if (!string.Equals(address.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(address.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An HTTP probe address must use the http or https scheme.", nameof(address));
        }

        var endpointAddress = new EndpointAddress(address.Scheme, address.Host, address.Port, address.EndpointPath);
        return new ProbeDefinition(ProbeKind.Http, endpoint: null, endpointAddress, address.EndpointPath, Array.Empty<string>());
    }

    /// <summary>Creates a TCP probe against a named resource endpoint.</summary>
    /// <param name="endpoint">The logical endpoint name.</param>
    /// <returns>A TCP probe specification.</returns>
    /// <exception cref="ArgumentException"><paramref name="endpoint"/> is empty.</exception>
    public static IProbeSpec Tcp(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return new ProbeDefinition(ProbeKind.Tcp, endpoint, address: null, path: null, Array.Empty<string>());
    }

    /// <summary>Creates a TCP probe against an absolute host and port.</summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The TCP port.</param>
    /// <returns>A TCP probe specification.</returns>
    /// <exception cref="ArgumentException"><paramref name="host"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not a valid port.</exception>
    public static IProbeSpec Tcp(string host, int port)
    {
        var address = new EndpointAddress("tcp", host, port);
        return new ProbeDefinition(ProbeKind.Tcp, endpoint: null, address, path: null, Array.Empty<string>());
    }

    /// <summary>Creates a probe that executes a command and succeeds when it exits with code zero.</summary>
    /// <param name="executable">The executable path or command name.</param>
    /// <param name="arguments">The command arguments.</param>
    /// <returns>An exec probe specification.</returns>
    /// <exception cref="ArgumentException"><paramref name="executable"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is <see langword="null"/>.</exception>
    public static IProbeSpec Exec(string executable, params string[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var command = new string[arguments.Length + 1];
        command[0] = executable;
        Array.Copy(arguments, 0, command, 1, arguments.Length);
        return new ProbeDefinition(ProbeKind.Exec, endpoint: null, address: null, path: null, command);
    }

    /// <summary>Creates a probe specification that explicitly disables a probe role.</summary>
    /// <returns>A disabled probe specification.</returns>
    public static IProbeSpec None()
        => new ProbeDefinition(ProbeKind.None, endpoint: null, address: null, path: null, Array.Empty<string>());
}
