using System;
using System.Net;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// Connection settings for a database client: the database to bind to, the
/// principal to claim, the server endpoint, and the pool size.
/// </summary>
/// <remarks>
/// Settings parse from a <c>key=value;</c> connection string
/// (<see cref="Parse(string)"/>) or compose directly as a typed object. The
/// transport driver itself is never named in the string — drivers are composed
/// statically (an <c>IConnectionFactory</c> on <see cref="DatabaseClientOptions"/>)
/// so no runtime plugin loading is needed; the string only carries endpoint
/// <em>identity</em>. Non-network endpoints (the in-memory transport's named
/// endpoints, socket files) are typed objects — set <see cref="EndPoint"/>
/// directly for those.
/// </remarks>
public sealed class DatabaseConnectionSettings
{
    /// <summary>
    /// The default size of the connection pool.
    /// </summary>
    public const int DefaultMaxPoolSize = 8;

    /// <summary>
    /// Gets or sets the name of the database sessions bind to.
    /// </summary>
    public string? Database { get; set; }

    /// <summary>
    /// Gets or sets the principal name the client claims during authentication.
    /// </summary>
    public string Principal { get; set; } = "anonymous";

    /// <summary>
    /// Gets or sets the server endpoint handed to the connection factory. Parsed
    /// connection strings produce a <see cref="DnsEndPoint"/> from the
    /// <c>Endpoint=host[:port]</c> key. Resource endpoints preserve IP literals as
    /// <see cref="IPEndPoint"/> values and host names as <see cref="DnsEndPoint"/> values;
    /// transports with non-network addressing take a typed endpoint here instead.
    /// </summary>
    public EndPoint? EndPoint { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of pooled connections per client.
    /// </summary>
    public int MaxPoolSize { get; set; } = DefaultMaxPoolSize;

    /// <summary>
    /// The default port assumed when a connection string endpoint omits one.
    /// </summary>
    public const int DefaultPort = 5740;

    /// <summary>
    /// Creates client settings for a resource endpoint supplied by the Cohesion
    /// application model or ambient resource context.
    /// </summary>
    /// <param name="endpoint">The resolved endpoint address.</param>
    /// <param name="database">The optional database to bind to.</param>
    /// <param name="principal">The principal to claim during authentication.</param>
    /// <returns>Connection settings that target <paramref name="endpoint"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="endpoint"/> is uninitialized or
    /// <paramref name="principal"/> is null or whitespace.
    /// </exception>
    public static DatabaseConnectionSettings For(
        EndpointAddress endpoint,
        string? database = null,
        string principal = "anonymous")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        if (string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port is <= 0 or > 65535)
        {
            throw new ArgumentException(
                "The database endpoint must have a host and a valid positive port.",
                nameof(endpoint));
        }

        EndPoint endPoint = IPAddress.TryParse(endpoint.Host, out IPAddress? address)
            ? new IPEndPoint(address, endpoint.Port)
            : new DnsEndPoint(endpoint.Host, endpoint.Port);

        return new DatabaseConnectionSettings
        {
            Database = database,
            Principal = principal,
            EndPoint = endPoint,
        };
    }

    /// <summary>
    /// Parses a <c>key=value;</c> connection string. Supported keys
    /// (case-insensitive): <c>Database</c>, <c>Principal</c>,
    /// <c>Endpoint</c> (<c>host</c> or <c>host:port</c>), <c>MaxPoolSize</c>.
    /// </summary>
    /// <param name="connectionString">The connection string to parse.</param>
    /// <returns>The parsed settings.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="connectionString"/> is null or whitespace.</exception>
    /// <exception cref="DatabaseClientException">Thrown when the string carries a malformed pair, an unknown key, or an invalid value.</exception>
    public static DatabaseConnectionSettings Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var settings = new DatabaseConnectionSettings();

        foreach (string segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = segment.IndexOf('=');

            if (separator <= 0 || separator == segment.Length - 1)
            {
                throw new DatabaseClientException(ProtocolErrorCode.Internal, $"Malformed connection string segment '{segment}'; expected key=value.");
            }

            string key = segment[..separator].Trim();
            string value = segment[(separator + 1)..].Trim();

            switch (key.ToLowerInvariant())
            {
                case "database":
                    settings.Database = value;
                    break;

                case "principal":
                    settings.Principal = value;
                    break;

                case "endpoint":
                    settings.EndPoint = ParseEndPoint(value);
                    break;

                case "maxpoolsize":
                    if (!int.TryParse(value, out int poolSize) || poolSize <= 0)
                    {
                        throw new DatabaseClientException(ProtocolErrorCode.Internal, $"Invalid MaxPoolSize value '{value}'; expected a positive integer.");
                    }

                    settings.MaxPoolSize = poolSize;
                    break;

                default:
                    throw new DatabaseClientException(ProtocolErrorCode.Internal, $"Unknown connection string key '{key}'.");
            }
        }

        return settings;
    }

    private static DnsEndPoint ParseEndPoint(string value)
    {
        string host = value;
        int port = DefaultPort;

        // IPv6 literals with ports use [host]:port; a bare colon split would
        // mangle them, so only split on the colon after a closing bracket or in
        // bracket-free values.
        int portSeparator;

        if (value.StartsWith('['))
        {
            int bracketEnd = value.IndexOf("]:", StringComparison.Ordinal);
            portSeparator = bracketEnd >= 0 ? bracketEnd + 1 : -1;
        }
        else
        {
            portSeparator = value.LastIndexOf(':');
        }

        if (portSeparator > 0)
        {
            host = value[..portSeparator].Trim('[', ']');

            if (!int.TryParse(value[(portSeparator + 1)..], out port) || port is <= 0 or > ushort.MaxValue)
            {
                throw new DatabaseClientException(ProtocolErrorCode.Internal, $"Invalid endpoint port in '{value}'.");
            }
        }
        else
        {
            host = host.Trim('[', ']');
        }

        if (host.Length == 0)
        {
            throw new DatabaseClientException(ProtocolErrorCode.Internal, $"Invalid endpoint '{value}'; expected host or host:port.");
        }

        return new DnsEndPoint(host, port);
    }
}
