using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Blob.Catalog;

/// <summary>Reports malformed or unsupported persisted blob catalog metadata.</summary>
public sealed class BlobCatalogException : Exception
{
    /// <summary>Gets the structured diagnostics; empty when none were supplied.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = Array.Empty<Diagnostic>();

    internal bool IsFormatRefusal { get; init; }

    internal BlobCatalogException(string code, string message, Exception? innerException = null)
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
    public BlobCatalogException(string message) : base(message) { }

    /// <summary>Initializes a catalog error with its decoding cause.</summary>
    /// <param name="message">The error description.</param>
    /// <param name="innerException">The underlying decoding error.</param>
    public BlobCatalogException(string message, Exception innerException) : base(message, innerException) { }
}
