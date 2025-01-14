namespace Assimalign.Cohesion.Configuration;

public record class ConfigurationEntry : IConfigurationValue
{
    public ConfigurationEntry(KeyPath path, object? value)
    {
        Path = path;
        Value = value;
    }
    public Key Key => Path.GetLast();
    public KeyPath Path { get; }
    public object? Value { get; }
    public IConfigurationProvider Provider => throw new System.NotImplementedException();
}
