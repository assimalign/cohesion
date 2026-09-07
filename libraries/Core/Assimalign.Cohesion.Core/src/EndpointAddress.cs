using System;

namespace Assimalign.Cohesion.Core;

/// <summary>
/// Represents the network address of a realized resource endpoint.
/// </summary>
public readonly record struct EndpointAddress
{
    /// <summary>
    /// Initializes a new endpoint address.
    /// </summary>
    /// <param name="scheme">The URI scheme, such as <c>http</c>, <c>https</c>, or <c>tcp</c>.</param>
    /// <param name="host">The DNS name or IP address of the endpoint.</param>
    /// <param name="port">The endpoint port in the range 1 through 65535.</param>
    /// <param name="path">The optional absolute URI path.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="scheme"/> or <paramref name="host"/> is empty, or when the
    /// supplied values do not form an absolute endpoint URI.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="port"/> is outside the range 1 through 65535.
    /// </exception>
    public EndpointAddress(string scheme, string host, int port, string? path = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (!Uri.CheckSchemeName(scheme))
        {
            throw new ArgumentException("The endpoint scheme is not a valid URI scheme.", nameof(scheme));
        }

        if (host.Contains('/') || host.Contains('?') || host.Contains('#') || host.Contains('@'))
        {
            throw new ArgumentException("The endpoint host contains an invalid URI authority character.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "The endpoint port must be between 1 and 65535.");
        }

        if (path is not null && (path.Contains('?') || path.Contains('#')))
        {
            throw new ArgumentException("The endpoint path cannot contain a query string or fragment.", nameof(path));
        }

        string? normalizedPath = NormalizePath(path);
        string candidate = Format(scheme, host, port, normalizedPath);

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("The supplied values do not form an absolute endpoint URI.");
        }

        Scheme = uri.Scheme;
        Host = uri.Host;
        Port = port;
        Path = uri.AbsolutePath == "/" ? null : uri.AbsolutePath;
    }

    /// <summary>
    /// Gets the normalized URI scheme.
    /// </summary>
    public string Scheme { get; }

    /// <summary>
    /// Gets the normalized DNS name or IP address.
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Gets the endpoint port.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Gets the optional absolute URI path.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// Gets the endpoint as an absolute URI.
    /// </summary>
    public Uri Url => new(ToString(), UriKind.Absolute);

    /// <summary>
    /// Parses an absolute endpoint URI.
    /// </summary>
    /// <param name="value">The absolute endpoint URI to parse.</param>
    /// <returns>The parsed endpoint address.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="value"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="FormatException">
    /// Thrown when <paramref name="value"/> is not an absolute endpoint URI with a host and valid port.
    /// </exception>
    public static EndpointAddress Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out EndpointAddress address))
        {
            throw new FormatException($"'{value}' is not a valid endpoint address.");
        }

        return address;
    }

    /// <summary>
    /// Attempts to parse an absolute endpoint URI.
    /// </summary>
    /// <param name="value">The absolute endpoint URI to parse.</param>
    /// <param name="address">The parsed endpoint address when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the value was parsed; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out EndpointAddress address)
    {
        address = default;

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrEmpty(uri.Host)
            || uri.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        try
        {
            address = new EndpointAddress(
                uri.Scheme,
                uri.Host,
                uri.Port,
                uri.AbsolutePath == "/" ? null : uri.AbsolutePath);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the absolute endpoint URI with its explicit port.
    /// </summary>
    /// <returns>The absolute endpoint URI.</returns>
    public override string ToString()
    {
        return Format(Scheme, Host, Port, Path);
    }

    private static string Format(string scheme, string host, int port, string? path)
    {
        string authorityHost = host.Contains(':') && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;

        return $"{scheme}://{authorityHost}:{port}{path}";
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return null;
        }

        return path.StartsWith("/", StringComparison.Ordinal) ? path : $"/{path}";
    }
}
