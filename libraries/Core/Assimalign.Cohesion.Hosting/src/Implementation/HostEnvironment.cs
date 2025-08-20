using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Hosting;

public class HostEnvironment : IHostEnvironment
{
    public HostEnvironment()
    {
    }

    [SetsRequiredMembers]
    public HostEnvironment(string name)
    {
        Name = name;
    }
    public required string? Name { get; init; }
    public bool IsEnvironment(string? environment)
    {
        return string.Equals(Name, environment, StringComparison.OrdinalIgnoreCase);
    }
}
