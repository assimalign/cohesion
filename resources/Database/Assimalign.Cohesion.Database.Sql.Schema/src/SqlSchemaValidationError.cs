using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Describes one typed schema validation error.</summary>
public sealed class SqlSchemaValidationError
{
    /// <summary>Initializes a schema validation error.</summary>
    /// <param name="code">The error category.</param>
    /// <param name="declaration">The offending declaration name.</param>
    /// <param name="message">The diagnostic message.</param>
    public SqlSchemaValidationError(
        SqlSchemaValidationErrorCode code,
        string declaration,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaration);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Declaration = declaration;
        Message = message;
    }

    /// <summary>Gets the error category.</summary>
    public SqlSchemaValidationErrorCode Code { get; }

    /// <summary>Gets the offending declaration name.</summary>
    public string Declaration { get; }

    /// <summary>Gets the diagnostic message.</summary>
    public string Message { get; }
}
