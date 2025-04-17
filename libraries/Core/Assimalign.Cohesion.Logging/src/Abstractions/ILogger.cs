namespace Assimalign.Cohesion.Logging;

public interface ILogger
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="entry"></param>
    void Log(ILoggerEntry entry);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IScopeLogger BeginScope(ILoggerEntry entry);
}