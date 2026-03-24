namespace Assimalign.Cohesion.Logging;

/// <summary>
/// 
/// </summary>
public interface ILoggerFactoryBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="provider"></param>
    /// <returns></returns>
    ILoggerFactoryBuilder AddProvider(ILoggerProvider provider);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    ILoggerFactory Build();
}
