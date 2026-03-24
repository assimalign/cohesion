namespace Assimalign.Cohesion.Logging;

/// <summary>
/// 
/// </summary>
public interface ILogger
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="level"></param>
    /// <returns></returns>
    bool IsEnabled(LogLevel level);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    void Log(ILoggerEntry entry);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IScopedLogger BeginScope(ILoggerEntry entry);
}