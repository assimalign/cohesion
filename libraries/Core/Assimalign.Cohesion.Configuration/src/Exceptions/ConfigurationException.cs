using System;

namespace Assimalign.Cohesion.Configuration;

public class ConfigurationException : CohesionException
{
    public ConfigurationException(string message) 
        : base(message)
    {
    }
    public ConfigurationException(string message, Exception innerException) 
        : base(message, innerException)
    {
    }
}
