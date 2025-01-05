using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public class ConfigurationEntry : IConfigurationEntry
{
    public ConfigurationEntry()
    {
    }


    public required ConfigKey Key { get; init; }
    public ConfigPath Path => throw new NotImplementedException();

    public object Value { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
}
