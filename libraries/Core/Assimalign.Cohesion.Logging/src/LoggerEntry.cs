using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Logging;

using Assimalign.Cohesion.Internal;

public class LoggerEntry : ILoggerEntry
{

    public LoggerEntry(LogId id, LogLevel level, string message)
    {
        Id = id;
        Level = ThrowHelper.ThrowIfNotDefined(level);
        Message = ThrowHelper.ThrowIfNullOrEmpty(message);
    }

    public LoggerEntry(LogId id, LogId parentId, LogLevel level, string message)
    {
        Id = id;
        Level = ThrowHelper.ThrowIfNotDefined(level);
        Message = ThrowHelper.ThrowIfNullOrEmpty(message);
    }
    public LogId Id { get; set; } = LogId.NewLogId();
    public LogId? ParentId { get; set; }
    public LogLevel Level { get; set; }
    public string? Message { get; set; }
}
