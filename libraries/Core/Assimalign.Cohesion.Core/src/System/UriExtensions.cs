using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System;

/// <summary>
/// Provides endpoint validation, construction, parsing, and canonical formatting for <see cref="Uri"/> values.
/// </summary>
/// <remarks>
/// Endpoint URI text uses <see cref="Uri.Host"/> so IPv6 literals retain their brackets. Code that passes
/// a host to a socket API uses <see cref="Uri.IdnHost"/> by convention so IPv6 literals are unbracketed and
/// internationalized domain names are represented in their ASCII-compatible form.
/// </remarks>
public static class UriExtensions
{
    extension(Uri)
    {
        /// <summary>
        /// Creates an absolute endpoint URI from its component values.
        /// </summary>
        /// <param name="scheme">The URI scheme, such as <c>http</c>, <c>https</c>, or <c>tcp</c>.</param>
        /// <param name="host">The DNS name or IP address of the endpoint.</param>
        /// <param name="port">The explicit endpoint port in the range 1 through 65535.</param>
        /// <param name="path">The optional endpoint path.</param>
        /// <returns>A normalized absolute endpoint URI.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="scheme"/> or <paramref name="host"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="scheme"/> or <paramref name="host"/> is empty, when a component
        /// contains a character that is not valid for its endpoint position, or when the supplied values
        /// do not form an absolute endpoint URI.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="port"/> is outside the range 1 through 65535.
        /// </exception>
        public static Uri CreateEndpoint(string scheme, string host, int port, string? path = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
            ArgumentException.ThrowIfNullOrWhiteSpace(host);

            if (!Uri.CheckSchemeName(scheme))
            {
                throw new ArgumentException("The endpoint scheme is not a valid URI scheme.", nameof(scheme));
            }

            if (ContainsInvalidHostCharacter(host))
            {
                throw new ArgumentException("The endpoint host contains an invalid URI authority character.", nameof(host));
            }

            if (port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), port, "The endpoint port must be between 1 and 65535.");
            }

            if (ContainsInvalidPathCharacter(path))
            {
                throw new ArgumentException("The endpoint path cannot contain a query string or fragment.", nameof(path));
            }

            if (!TryCreateEndpointCore(scheme, host, port, path, out Uri? endpoint))
            {
                throw new ArgumentException("The supplied values do not form an absolute endpoint URI.");
            }

            return endpoint;
        }

        /// <summary>
        /// Attempts to create an absolute endpoint URI from its component values.
        /// </summary>
        /// <param name="scheme">The URI scheme, such as <c>http</c>, <c>https</c>, or <c>tcp</c>.</param>
        /// <param name="host">The DNS name or IP address of the endpoint.</param>
        /// <param name="port">The explicit endpoint port in the range 1 through 65535.</param>
        /// <param name="path">The optional endpoint path.</param>
        /// <param name="endpoint">The normalized endpoint URI when this method returns <see langword="true"/>.</param>
        /// <returns><see langword="true"/> when the values form an endpoint URI; otherwise, <see langword="false"/>.</returns>
        public static bool TryCreateEndpoint(
            string? scheme,
            string? host,
            int port,
            string? path,
            [NotNullWhen(true)] out Uri? endpoint)
        {
            endpoint = null;

            if (string.IsNullOrWhiteSpace(scheme)
                || string.IsNullOrWhiteSpace(host)
                || !Uri.CheckSchemeName(scheme)
                || ContainsInvalidHostCharacter(host)
                || port is < 1 or > 65535
                || ContainsInvalidPathCharacter(path))
            {
                return false;
            }

            return TryCreateEndpointCore(scheme, host, port, path, out endpoint);
        }

        /// <summary>
        /// Attempts to parse an absolute URI that satisfies the Cohesion endpoint shape.
        /// </summary>
        /// <param name="value">The absolute endpoint URI text to parse.</param>
        /// <param name="endpoint">The parsed endpoint URI when this method returns <see langword="true"/>.</param>
        /// <returns><see langword="true"/> when <paramref name="value"/> is an endpoint URI; otherwise, <see langword="false"/>.</returns>
        public static bool TryParseEndpoint(string? value, [NotNullWhen(true)] out Uri? endpoint)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out endpoint) && endpoint.IsEndpoint)
            {
                return true;
            }

            endpoint = null;
            return false;
        }

        /// <summary>
        /// Returns an endpoint URI after validating the Cohesion endpoint shape.
        /// </summary>
        /// <param name="endpoint">The URI to validate.</param>
        /// <param name="paramName">The name of the argument being validated.</param>
        /// <returns><paramref name="endpoint"/> when it is a valid endpoint URI.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="endpoint"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="endpoint"/> is not an endpoint URI.</exception>
        public static Uri ThrowIfNotEndpoint(
            [NotNull] Uri? endpoint,
            [CallerArgumentExpression(nameof(endpoint))] string? paramName = null)
        {
            if (endpoint is null)
            {
                throw new ArgumentNullException(paramName);
            }

            if (!endpoint.IsEndpoint)
            {
                throw new ArgumentException("The URI must be an absolute endpoint with a host and explicit port, without user information, a query, or a fragment.", paramName);
            }

            return endpoint;
        }
    }

    extension(Uri uri)
    {
        /// <summary>
        /// Gets a value indicating whether the URI is an absolute Cohesion endpoint with a host and explicit port,
        /// without user information, a query, or a fragment.
        /// </summary>
        public bool IsEndpoint
            => uri.IsAbsoluteUri
                && uri.Host.Length > 0
                && uri.Port is >= 1 and <= 65535
                && uri.UserInfo.Length == 0
                && uri.Query.Length == 0
                && uri.Fragment.Length == 0;

        /// <summary>
        /// Gets the escaped absolute endpoint path, or <see langword="null"/> for the root path.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="uri"/> is relative.</exception>
        public string? EndpointPath => uri.AbsolutePath == "/" ? null : uri.AbsolutePath;

        /// <summary>
        /// Formats the URI in the canonical Cohesion endpoint form with an explicit port, an escaped path,
        /// and no trailing slash for the root path.
        /// </summary>
        /// <returns>The canonical endpoint string.</returns>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="uri"/> is not an endpoint URI.</exception>
        public string ToEndpointString()
        {
            if (!uri.IsEndpoint)
            {
                throw new InvalidOperationException("The URI is not a valid endpoint.");
            }

            return $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.EndpointPath}";
        }
    }

    private static bool TryCreateEndpointCore(
        string scheme,
        string host,
        int port,
        string? path,
        [NotNullWhen(true)] out Uri? endpoint)
    {
        string authorityHost = host.Contains(':') && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
        string? normalizedPath = string.IsNullOrEmpty(path) || path == "/"
            ? null
            : path.StartsWith("/", StringComparison.Ordinal) ? path : $"/{path}";
        string candidate = $"{scheme}://{authorityHost}:{port}{normalizedPath}";

        if (Uri.TryCreate(candidate, UriKind.Absolute, out endpoint) && endpoint.IsEndpoint)
        {
            return true;
        }

        endpoint = null;
        return false;
    }

    private static bool ContainsInvalidHostCharacter(string host)
        => host.Contains('/') || host.Contains('?') || host.Contains('#') || host.Contains('@');

    private static bool ContainsInvalidPathCharacter(string? path)
        => path is not null && (path.Contains('?') || path.Contains('#'));
}
