using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Marks an unsupported query shape discovered during catalog binding, so the
/// execution boundary returns the same language diagnostic as parser exclusions.
/// </summary>
/// <param name="message">The unsupported form and its supported boundary.</param>
/// <param name="innerException">The binding failure that identified the form.</param>
internal sealed class SqlUnsupportedQueryException(string message, Exception? innerException = null)
    : DatabaseException(message, innerException);
