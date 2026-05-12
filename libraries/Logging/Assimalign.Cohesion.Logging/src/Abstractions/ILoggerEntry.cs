using System;

namespace Assimalign.Cohesion.Logging;

public interface ILoggerEntry
{
    /// <summary>
    /// A unique identifier for the log entry.
    /// </summary>
    LogId Id { get; }

    /// <summary>
    /// Parent Id represents a hierarchal log structure where as there is a sequence of events to be tracked. Optional
    /// </summary>
    LogId? ParentId { get; }

    /// <summary>
    /// The severity of the log entry.
    /// </summary>
    LogLevel Level { get; }

    /// <summary>
    /// The log entry message.
    /// </summary>
    string? Message { get; }
}
