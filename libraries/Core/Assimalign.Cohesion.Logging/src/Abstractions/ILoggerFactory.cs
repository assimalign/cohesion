using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// 
/// </summary>
public interface ILoggerFactory : IDisposable
{
    /// <summary>
    /// 
    /// </summary>
    IEnumerable<ILoggerProvider> Providers { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="loggerName">The name of the logger.</param>
    /// <returns></returns>
    ILogger Create(string loggerName);
}
