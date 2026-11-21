using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A named <see cref="IConnectionFeature"/> a test sets on a carrier to observe that the AMQP connection
/// exposes the carrier's features.
/// </summary>
internal sealed class TestConnectionFeature : IConnectionFeature
{
    public TestConnectionFeature(string name)
    {
        Name = name;
    }

    public string Name { get; }
}
