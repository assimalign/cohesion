using System;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Database.Internal;

internal static class DatabasePlatformEndpoint
{
    public static IDuplexConnectionListener? CreateListener(string model)
    {
        string? name = model switch
        {
            "SQL" => "sql",
            "Documents" => "documents",
            "Graph" => "graph",
            "KeyValuePair" => "kv",
            "Blob" => "blob",
            _ => null,
        };

        // Follow Web.Hosting's HttpServerConfiguration.GetPlatformEndPoint: read the current
        // AppEnvironment scope, never process-global environment variables or configuration.
        // As with Listen(Uri), the assigned scheme does not select a transport or enable TLS.
        return name is not null && AppEnvironment.TryGetEndpoint(name, out Uri? endpoint)
            ? CreateTcpListener(endpoint)
            : null;
    }

    // Unmanaged engine consumers may use an in-memory listener without deploying TCP. Keep
    // its concrete types out of the caller's JIT import until an assigned endpoint needs them.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDuplexConnectionListener CreateTcpListener(Uri endpoint)
        => DatabaseTcpEndpoint.CreateListener(endpoint);
}
