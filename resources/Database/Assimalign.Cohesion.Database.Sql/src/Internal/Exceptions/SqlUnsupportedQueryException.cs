using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Marks an unsupported query shape discovered during catalog binding, so the
/// execution boundary returns the same language diagnostic as parser exclusions.
/// </summary>
internal sealed class SqlUnsupportedQueryException : DatabaseException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SqlUnsupportedQueryException"/> class.
    /// </summary>
    /// <param name="message">The unsupported form and its supported boundary.</param>
    /// <param name="innerException">The binding failure that identified the form.</param>
    public SqlUnsupportedQueryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
