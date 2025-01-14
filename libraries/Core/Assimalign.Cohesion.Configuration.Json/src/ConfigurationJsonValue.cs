namespace Assimalign.Cohesion.Configuration;

public class ConfigurationJsonValue : ConfigurationJsonEntry, IConfigurationValue
{
    public ConfigurationJsonValue(KeyPath path, object? value)
    {
        
    }
    public override Key Key => Path.GetLast();
    public KeyPath Path { get; }
    public object? Value { get; }
}
