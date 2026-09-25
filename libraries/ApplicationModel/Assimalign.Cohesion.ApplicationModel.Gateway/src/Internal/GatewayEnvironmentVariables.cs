using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

internal static class GatewayEnvironmentVariables
{
    public static void Set(
        IDictionary<string, string> environment,
        string name,
        string value)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        Remove(environment, name);
        environment[name] = value;
    }

    public static void Remove(IDictionary<string, string> environment, string name)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        environment.Remove(name);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        List<string>? aliases = null;
        foreach (string candidate in environment.Keys)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                aliases ??= new List<string>();
                aliases.Add(candidate);
            }
        }

        if (aliases is null)
        {
            return;
        }

        foreach (string alias in aliases)
        {
            environment.Remove(alias);
        }
    }
}
