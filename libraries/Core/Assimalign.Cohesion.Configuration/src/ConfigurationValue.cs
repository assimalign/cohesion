namespace Assimalign.Cohesion.Configuration;

public record class ConfigurationValue : IConfigurationValue
{
    public ConfigurationValue(KeyPath path, object? value)
    {
        Path = path;
        Value = value;
    }
    public Key Key => Path.GetLastKey();
    public KeyPath Path { get; }
    public object? Value { get; }
}
