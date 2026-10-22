namespace Assimalign.Cohesion.Connections.Tests;

internal sealed class NamedLookupCollection : ConnectionFeatureCollection, IConnectionFeatureCollection
{
    private readonly IConnectionFeature _feature;

    internal NamedLookupCollection(IConnectionFeature feature)
    {
        _feature = feature;
        Set(new TestConnectionFeature("BaseOnly"));
    }

    internal int Calls { get; private set; }

    IConnectionFeature? IConnectionFeatureCollection.Get(string name)
    {
        Calls++;
        return name == _feature.Name ? _feature : null;
    }
}
