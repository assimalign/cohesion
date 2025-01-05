using System;

namespace Assimalign.Cohesion.Hosting.Internal;

internal class HostEnvironment : IHostEnvironment
{
    public string? Name { get; init; }
    public bool IsEnvironment(string? environment)
    {
        return string.Equals(Name, environment, StringComparison.OrdinalIgnoreCase);
    }
}
