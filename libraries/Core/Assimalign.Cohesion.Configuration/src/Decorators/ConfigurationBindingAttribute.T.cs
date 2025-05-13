using System;

namespace Assimalign.Cohesion.Configuration;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ConfigurationBindingAttribute<T> : ConfigurationBindingAttribute
    where T : new()
{
    public ConfigurationBindingAttribute() : base(typeof(T))
    {

    }
}
