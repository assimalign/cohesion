namespace Assimalign.Cohesion.Connections.Tests;

internal sealed class TestConnectionFeature : IConnectionFeature
{
    public TestConnectionFeature(string name)
    {
        Name = name;
    }

    public string Name { get; }
}
