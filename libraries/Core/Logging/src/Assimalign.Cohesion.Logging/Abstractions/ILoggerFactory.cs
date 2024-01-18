namespace Assimalign.Cohesion.Net.Logging;

/// <summary>
/// 
/// </summary>
public interface ILoggerFactory
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="loggerName"></param>
    /// <returns></returns>
    ILogger Create(string loggerName);
}
