using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

[AttributeUsage(AttributeTargets.Property)]
public sealed class ConfigurationKeyNameAttribute : Attribute
{
    public ConfigurationKeyNameAttribute(string name) => Name = name;

    public string Name { get; }
}
