using System;

namespace Assimalign.Cohesion.Database.Graph.Catalog;

/// <summary>Reports invalid graph metadata or a conflicting catalog definition.</summary>
public sealed class GraphCatalogException : Exception
{
    // Mutation conflicts are retryable; decode failures describe damaged catalog records.
    internal bool IsConflict { get; init; }

    /// <summary>Initializes a catalog error.</summary>
    /// <param name="message">The error description.</param>
    public GraphCatalogException(string message) : base(message) { }

    /// <summary>Initializes a catalog error with the underlying decoding cause.</summary>
    /// <param name="message">The error description.</param>
    /// <param name="innerException">The underlying error.</param>
    public GraphCatalogException(string message, Exception innerException) : base(message, innerException) { }
}
