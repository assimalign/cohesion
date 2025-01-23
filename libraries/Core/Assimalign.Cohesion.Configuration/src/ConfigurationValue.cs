using System.Diagnostics;

namespace Assimalign.Cohesion.Configuration;

[DebuggerDisplay("{Key}: {Value}")]
public class ConfigurationValue : IConfigurationValue
{
    public ConfigurationValue(Key key, object? value)
    {
        Key = key;
        Value = value;
    }

    /// <summary>
    /// 
    /// </summary>
    public Key Key { get; }

    /// <summary>
    /// 
    /// </summary>
    public object? Value { get; }
}
