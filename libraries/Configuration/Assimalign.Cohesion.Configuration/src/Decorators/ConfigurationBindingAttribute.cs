using System;

namespace Assimalign.Cohesion.Configuration;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class ConfigurationBindingAttribute : Attribute
{
    public ConfigurationBindingAttribute(Type type)
    {
        Type = type;
    }


    public Type Type { get; }
}
