namespace Assimalign.Cohesion.Configuration;

public record class ConfigurationEntry : IConfigurationEntry
{
    public ConfigurationEntry(Key key, object? value)
    {
        Key = key;
        Value = value;
    }
    public Key Key { get; }
    public object? Value { get; }
}
