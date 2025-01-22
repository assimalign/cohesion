using System;

namespace Assimalign.Cohesion.Hosting;

public sealed class HostTrace
{
    public Guid? Id { get; } = Guid.NewGuid();
    public string? Message { get; init; }
}
