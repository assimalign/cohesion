using System;

namespace Assimalign.Cohesion.Database.KeyValuePair.Catalog;

/// <summary>
/// Thrown when a key-value catalog operation fails: a malformed persisted record,
/// or an invalid metadata write.
/// </summary>
public class KeyValueCatalogException : DatabaseException
{
    /// <summary>
    /// Initializes a new <see cref="KeyValueCatalogException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    public KeyValueCatalogException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="KeyValueCatalogException"/> with an inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public KeyValueCatalogException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
