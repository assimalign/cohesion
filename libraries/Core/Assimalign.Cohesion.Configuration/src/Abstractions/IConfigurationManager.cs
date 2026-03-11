using System;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Defines methods for dynamically adding configuration providers to the configuration system at runtime.
/// </summary>
public interface IConfigurationManager : IConfiguration
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="provider"></param>
    /// <returns></returns>
    IConfigurationManager AddProvider(IConfigurationProvider provider);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="provider"></param>
    /// <returns></returns>
    IConfigurationManager AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> provider);
}