using System;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// A logger batch is a logical grouping of logs
/// </summary>
/// <remarks>
/// This is useful when there are multiple processes running asynchronously
/// </remarks>
public interface IScopedLogger : ILogger, IDisposable
{
    /// <summary>
    /// 
    /// </summary>
    LogId ParentId { get; }
}
