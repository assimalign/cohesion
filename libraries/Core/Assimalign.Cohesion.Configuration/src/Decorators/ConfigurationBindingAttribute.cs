using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class ConfigurationBindingAttribute : Attribute
{
    public ConfigurationBindingAttribute(Type type)
    {
        Type = type;
    }

    public Type Type { get; set; }
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ConfigurationBindingAttribute<T> : ConfigurationBindingAttribute
    where T : new()
{
    public ConfigurationBindingAttribute() : base(typeof(T))
    {
        
    }
}
