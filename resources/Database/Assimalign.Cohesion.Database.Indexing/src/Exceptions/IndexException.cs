using System;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Base exception type for errors raised by the index infrastructure.
/// </summary>
/// <remarks>
/// An independent exception root: it inherits <see cref="Exception"/>, not the
/// area's <c>DatabaseException</c> — the Indexing package is a child root the
/// area root rolls up, so it stays independently consumable. Layers that own
/// both vocabularies (the model engines) translate index failures at their
/// boundary.
/// </remarks>
public class IndexException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="IndexException"/> with a message.
    /// </summary>
    /// <param name="message">Error message describing the failure.</param>
    public IndexException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="IndexException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">Error message describing the failure.</param>
    /// <param name="inner">Underlying cause of the failure.</param>
    public IndexException(string message, Exception inner) : base(message, inner)
    {
    }
}
