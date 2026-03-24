using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Configuration;

public class ConfigurationException : CohesionException
{
    public ConfigurationException(ConfigurationErrorCode code, string message) 
        : base(message)
    {
        Code = code;
    }

    public ConfigurationException(ConfigurationErrorCode code, string message, Exception innerException) 
        : base(message, innerException)
    {
        Code = code;
    }

    public ConfigurationErrorCode Code { get; }


    [DoesNotReturn]
    public static void ThrowNotFound()
    {
        throw NotFound;
    }


    internal static ConfigurationException NotFound => new ConfigurationException(
            ConfigurationErrorCode.NotFound,
            "The specified configuration section or value was not found.");
}
