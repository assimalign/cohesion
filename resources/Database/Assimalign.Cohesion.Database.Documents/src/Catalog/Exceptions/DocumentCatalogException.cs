using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>Reports malformed or unsupported persisted document catalog metadata.</summary>
public sealed class DocumentCatalogException : Exception
{
    /// <summary>Gets the structured diagnostics; empty when none were supplied.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = Array.Empty<Diagnostic>();

    // User errors retain their identity across the engine boundary without parsing message text.
    internal string? Code { get; }
    internal bool IsFormatRefusal { get; init; }
    internal PageId? PageId { get; init; }
    internal int? SlotIndex { get; init; }

    internal DocumentCatalogException(string code, string message)
        : this(code, message, null) { }

    internal DocumentCatalogException(string? code, string message, Exception? innerException) : base(message, innerException)
    {
        Code = code ?? "CD06105";
        Diagnostics = Array.AsReadOnly<Diagnostic>([new Diagnostic
        {
            Code = Code,
            Message = message,
            Severity = DiagnosticSeverity.Error,
        }]);
    }

    /// <summary>Initializes a catalog error.</summary>
    /// <param name="message">The error description.</param>
    public DocumentCatalogException(string message) : base(message) { }

    /// <summary>Initializes a catalog error with its decoding cause.</summary>
    /// <param name="message">The error description.</param>
    /// <param name="innerException">The underlying decoding error.</param>
    public DocumentCatalogException(string message, Exception innerException) : base(message, innerException) { }
}

