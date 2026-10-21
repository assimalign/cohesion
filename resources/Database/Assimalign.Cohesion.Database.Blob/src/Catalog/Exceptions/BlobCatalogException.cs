using System;

namespace Assimalign.Cohesion.Database.Blob.Catalog;

/// <summary>Reports malformed or unsupported persisted blob catalog metadata.</summary>
public sealed class BlobCatalogException : Exception
{
    /// <summary>Initializes a catalog error.</summary>
    /// <param name="message">The error description.</param>
    public BlobCatalogException(string message) : base(message) { }

    /// <summary>Initializes a catalog error with its decoding cause.</summary>
    /// <param name="message">The error description.</param>
    /// <param name="innerException">The underlying decoding error.</param>
    public BlobCatalogException(string message, Exception innerException) : base(message, innerException) { }
}
