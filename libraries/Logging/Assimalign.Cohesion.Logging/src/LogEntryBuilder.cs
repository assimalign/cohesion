using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Fluent builder for <see cref="LogEntry"/>. Use through <see cref="LoggerExtensions"/> or
/// directly when a caller needs more control than the typed log methods give.
/// </summary>
public sealed class LogEntryBuilder
{
    private readonly Dictionary<string, object?> _attributes = new(StringComparer.Ordinal);
    private LogId? _parentId;
    private LogId? _id;
    private DateTimeOffset? _timestamp;
    private string _category;
    private LogLevel _level;
    private string? _message;
    private Exception? _exception;

    /// <summary>
    /// Initializes a builder.
    /// </summary>
    /// <param name="level">Event severity.</param>
    /// <param name="category">Logger category. Required, non-empty.</param>
    /// <exception cref="ArgumentException"><paramref name="category"/> is null or empty.</exception>
    public LogEntryBuilder(LogLevel level, string category)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);

        _level = level;
        _category = category;
    }

    /// <summary>Sets the event severity.</summary>
    public LogEntryBuilder WithLevel(LogLevel level)
    {
        _level = level;
        return this;
    }

    /// <summary>Sets the logger category.</summary>
    /// <exception cref="ArgumentException"><paramref name="category"/> is null or empty.</exception>
    public LogEntryBuilder WithCategory(string category)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);
        _category = category;
        return this;
    }

    /// <summary>Sets the human-readable message.</summary>
    public LogEntryBuilder WithMessage(string? message)
    {
        _message = message;
        return this;
    }

    /// <summary>Sets the optional exception associated with the event.</summary>
    public LogEntryBuilder WithException(Exception? exception)
    {
        _exception = exception;
        return this;
    }

    /// <summary>Sets the optional parent log id.</summary>
    public LogEntryBuilder WithParentId(LogId? parentId)
    {
        _parentId = parentId;
        return this;
    }

    /// <summary>Overrides the generated id.</summary>
    public LogEntryBuilder WithId(LogId id)
    {
        _id = id;
        return this;
    }

    /// <summary>Overrides the captured timestamp.</summary>
    public LogEntryBuilder WithTimestamp(DateTimeOffset timestamp)
    {
        _timestamp = timestamp;
        return this;
    }

    /// <summary>
    /// Adds a single attribute. Existing keys are overwritten with the new value.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is null or empty.</exception>
    public LogEntryBuilder WithAttribute(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _attributes[key] = value;
        return this;
    }

    /// <summary>
    /// Adds a batch of attributes. Existing keys are overwritten.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="attributes"/> is <see langword="null"/>.</exception>
    public LogEntryBuilder WithAttributes(IEnumerable<KeyValuePair<string, object?>> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        foreach (var pair in attributes)
        {
            _attributes[pair.Key] = pair.Value;
        }

        return this;
    }

    /// <summary>
    /// Returns the configured <see cref="LogEntry"/>.
    /// </summary>
    public LogEntry Build()
    {
        IReadOnlyDictionary<string, object?>? attributes = _attributes.Count == 0
            ? null
            : new Dictionary<string, object?>(_attributes, StringComparer.Ordinal);

        return new LogEntry(
            level: _level,
            category: _category,
            message: _message,
            exception: _exception,
            attributes: attributes,
            parentId: _parentId,
            id: _id,
            timestamp: _timestamp);
    }
}
