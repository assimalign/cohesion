using System;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>Reports malformed or unsupported persisted document catalog metadata.</summary>
public sealed class DocumentCatalogException : Exception
{
    /// <summary>Initializes a catalog error.</summary>
    /// <param name="message">The error description.</param>
    public DocumentCatalogException(string message) : base(message) { }

    /// <summary>Initializes a catalog error with its decoding cause.</summary>
    /// <param name="message">The error description.</param>
    /// <param name="innerException">The underlying decoding error.</param>
    public DocumentCatalogException(string message, Exception innerException) : base(message, innerException) { }
}

