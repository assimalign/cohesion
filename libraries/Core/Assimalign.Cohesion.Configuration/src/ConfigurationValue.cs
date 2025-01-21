using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

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
