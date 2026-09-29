namespace Assimalign.Cohesion.Logging.Tests;

/// <summary>
/// Filter that admits every entry and carries a name, for registration and introspection tests.
/// </summary>
internal sealed class NamedFilter : ILoggerFilter
{
    public NamedFilter(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public bool ShouldLog(ILoggerEntry entry) => true;
}
