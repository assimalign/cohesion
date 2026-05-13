using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Immutable structured log event handed off to <see cref="ILogger.Log"/>.
/// </summary>
/// <remarks>
/// <para>
/// Log entries are the unit of work for the Cohesion logging pipeline. A consumer (provider or
/// enricher) reads the entry's <see cref="Level"/>, <see cref="Message"/>, <see cref="Exception"/>,
/// <see cref="Category"/>, <see cref="Timestamp"/>, and <see cref="Attributes"/>; it MUST NOT mutate
/// the entry. Construct entries through <see cref="LogEntryBuilder"/> or
/// <see cref="LoggerExtensions"/>.
/// </para>
/// <para>
/// <see cref="Attributes"/> carries structured key-value data for the event. Keys are case
/// sensitive. Values may be <see langword="null"/>; consumers that flatten attributes into a text
/// payload should render <see langword="null"/> as the literal string "null".
/// </para>
/// </remarks>
public interface ILogEntry
{
    /// <summary>
    /// Unique identifier for the entry. Monotonically increasing within a single process.
    /// </summary>
    LogId Id { get; }

    /// <summary>
    /// Optional parent identifier when the entry was produced inside an <see cref="IScopedLogger"/>.
    /// </summary>
    LogId? ParentId { get; }

    /// <summary>
    /// UTC wall-clock timestamp captured when the entry was constructed.
    /// </summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Severity of the event.
    /// </summary>
    LogLevel Level { get; }

    /// <summary>
    /// Logger category that produced the entry. Typically the fully qualified type name of the
    /// component that called the logger.
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Human-readable message. May be empty when <see cref="Exception"/> carries the diagnostic.
    /// </summary>
    string Message { get; }

    /// <summary>
    /// Optional exception associated with the event.
    /// </summary>
    Exception? Exception { get; }

    /// <summary>
    /// Optional structured attributes attached to the entry.
    /// </summary>
    /// <remarks>
    /// Implementations may return an empty dictionary; consumers should treat <see langword="null"/>
    /// and an empty dictionary identically.
    /// </remarks>
    IReadOnlyDictionary<string, object?> Attributes { get; }
}
