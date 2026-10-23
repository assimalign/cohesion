using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Catalog;

/// <summary>Reports invalid graph metadata or a conflicting catalog definition.</summary>
public sealed class GraphCatalogException : Exception
{
    /// <summary>Gets the structured diagnostics; empty when none were supplied.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = Array.Empty<Diagnostic>();

    // Only snapshot changes are retryable; caller errors and malformed records are distinct.
    internal bool IsConflict { get; init; }
    internal bool IsCallerError { get; init; }

    internal GraphCatalogException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Diagnostics = Array.AsReadOnly<Diagnostic>([new Diagnostic
        {
            Code = code,
            Message = message,
            Severity = DiagnosticSeverity.Error,
        }]);
    }

    /// <summary>Initializes a catalog error.</summary>
    /// <param name="message">The error description.</param>
    public GraphCatalogException(string message) : base(message) { }

    /// <summary>Initializes a catalog error with the underlying decoding cause.</summary>
    /// <param name="message">The error description.</param>
    /// <param name="innerException">The underlying error.</param>
    public GraphCatalogException(string message, Exception innerException) : base(message, innerException) { }
}
