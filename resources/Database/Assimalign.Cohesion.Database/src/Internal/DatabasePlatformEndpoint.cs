using System;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Database.Internal;

internal static class DatabasePlatformEndpoint
{
    public static IDuplexConnectionListener? CreateListener(string model)
    {
        if (string.IsNullOrWhiteSpace(AppEnvironment.GetValue(AppEnvironment.Variables.Gateway)))
        {
            return null;
        }

        string? name = model switch
        {
            "SQL" => "sql",
            "Documents" => "documents",
            "Graph" => "graph",
            "KeyValuePair" => "kv",
            "Blob" => "blob",
            _ => null,
        };

        // Read the current AppEnvironment flow: the scope when inside one, otherwise the process
        // environment. Only a managed application consumes the platform's default bind values.
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
