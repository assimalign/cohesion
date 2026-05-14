using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Default immutable implementation of <see cref="ILoggerEntry"/>.
/// </summary>
public sealed class LoggerEntry : ILoggerEntry
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyAttributes = new Dictionary<string, object?>(capacity: 0);

    /// <summary>
    /// Initializes a new entry.
    /// </summary>
    /// <param name="level">Event severity.</param>
    /// <param name="category">Logger category. Required, non-empty.</param>
    /// <param name="message">Human-readable message. Defaults to empty when null.</param>
    /// <param name="exception">Optional exception associated with the event.</param>
    /// <param name="attributes">Optional structured attributes.</param>
    /// <param name="parentId">Optional parent log id.</param>
    /// <param name="id">Optional explicit id. When omitted, a fresh <see cref="LogId"/> is generated.</param>
    /// <param name="timestamp">Optional timestamp. When omitted, captures <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="category"/> is null or empty.</exception>
    public LoggerEntry(
        LogLevel level,
        string category,
        string? message = null,
        Exception? exception = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        LogId? parentId = null,
        LogId? id = null,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);

        Id = id ?? LogId.New();
        ParentId = parentId;
        Level = level;
        Category = category;
        Message = message ?? string.Empty;
        Exception = exception;
        Timestamp = timestamp ?? DateTimeOffset.UtcNow;
        Attributes = attributes ?? EmptyAttributes;
    }

    /// <inheritdoc />
    public LogId Id { get; }

    /// <inheritdoc />
    public LogId? ParentId { get; }

    /// <inheritdoc />
    public DateTimeOffset Timestamp { get; }

    /// <inheritdoc />
    public LogLevel Level { get; }

    /// <inheritdoc />
    public string Category { get; }

    /// <inheritdoc />
    public string Message { get; }

    /// <inheritdoc />
    public Exception? Exception { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Attributes { get; }
}
