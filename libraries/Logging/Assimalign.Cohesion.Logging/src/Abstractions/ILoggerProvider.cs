using System;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// 
/// </summary>
public interface ILoggerProvider : IDisposable
{
    /// <summary>
    /// The name of the logging provider.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="loggerName"></param>
    /// <returns></returns>
    ILogger Create(string loggerName);
}
