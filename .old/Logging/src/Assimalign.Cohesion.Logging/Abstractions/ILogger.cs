namespace Assimalign.Cohesion.Logging;

public interface ILogger
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="level"></param>
    /// <param name="message"></param>
    void Log(LogLevel level, string message);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    ILoggerBatch CreateLogBatch();
}