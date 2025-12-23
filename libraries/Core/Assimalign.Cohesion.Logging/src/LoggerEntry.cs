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
        ArgumentNullException.ThrowIfNullOrEmpty(message);
        ArgumentException.ThrowIfEnumNotDefined(level);

        Id = id;
        Level = level;
        Message = message;
    }

    public LoggerEntry(LogId id, LogId parentId, LogLevel level, string message) : this(id, level, message)
    {
        Id = id;
        ParentId = parentId;
    }
    public LogId Id { get; set; } = LogId.New();
    public LogId? ParentId { get; set; }
    public LogLevel Level { get; set; }
    public string? Message { get; set; }
}
