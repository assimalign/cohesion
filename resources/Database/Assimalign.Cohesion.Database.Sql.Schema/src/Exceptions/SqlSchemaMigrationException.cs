using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Represents a migration planning or application failure.</summary>
public sealed class SqlSchemaMigrationException : DatabaseException
{
    /// <summary>Initializes a migration failure.</summary>
    /// <param name="message">The failure message.</param>
    public SqlSchemaMigrationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a migration failure with an underlying cause.</summary>
    /// <param name="message">The failure message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public SqlSchemaMigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
