using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public class ConfigurationEntry : IConfigurationEntry
{
    public ConfigurationEntry(Key key)
    {
        Path = key;
    }
    public Key Key { get; }
    public Key Path { get; }
    public object? Value { get; set; }
}
