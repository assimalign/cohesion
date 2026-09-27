using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging.Tests;

/// <summary>
/// Enricher that does nothing but carry a name, for registration and introspection tests.
/// </summary>
internal sealed class NamedEnricher : ILoggerEnricher
{
    public NamedEnricher(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public void Enrich(ILoggerEntry entry, IDictionary<string, object?> attributes)
    {
    }
}
