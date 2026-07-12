using System;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Represents a failure inside the shared execution substrate (pipeline
/// composition, stage contract violations). Model engines surface their own
/// domain errors through the area root exception; this root is scoped to the
/// execution layer, which sits below it in the dependency graph.
/// </summary>
public sealed class QueryExecutionException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="QueryExecutionException"/> with a message.
    /// </summary>
    /// <param name="message">Error message describing the failure.</param>
    public QueryExecutionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="QueryExecutionException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">Error message describing the failure.</param>
    /// <param name="innerException">Underlying cause of the failure.</param>
    public QueryExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
