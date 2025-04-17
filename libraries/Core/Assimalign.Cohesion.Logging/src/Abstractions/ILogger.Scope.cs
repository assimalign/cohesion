using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// A logger batch is a logical grouping of logs
/// </summary>
/// <remarks>
/// This is useful when there are multiple processes running asynchronously
/// </remarks>
public interface IScopeLogger : ILogger, IDisposable
{
    /// <summary>
    /// 
    /// </summary>
    LogId ParentId { get; }
}
