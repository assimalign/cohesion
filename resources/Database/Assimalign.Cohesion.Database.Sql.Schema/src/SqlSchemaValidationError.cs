using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Identifies a schema compilation validation failure.</summary>
public enum SqlSchemaValidationErrorCode : byte
{
    /// <summary>A declaration name is duplicated.</summary>
    DuplicateDeclaration = 0,

    /// <summary>A declaration is missing required information.</summary>
    IncompleteDeclaration,

    /// <summary>A declaration refers to an object that does not exist.</summary>
    UnknownReference,

    /// <summary>A CLR value cannot be represented by the selected database model.</summary>
    UnsupportedType,

    /// <summary>An expression cannot be represented in the stable schema AST.</summary>
    UnsupportedExpression,

    /// <summary>A compiled schema document is invalid or uses an unsupported format.</summary>
    InvalidDocument,

    /// <summary>The declaration is not valid for the selected engine model.</summary>
    ModelMismatch,
}

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

/// <summary>Represents one or more typed schema compilation failures.</summary>
public sealed class SqlSchemaValidationException : DatabaseException
{
    /// <summary>Initializes the exception from validation errors.</summary>
    /// <param name="errors">The validation errors.</param>
    public SqlSchemaValidationException(IReadOnlyList<SqlSchemaValidationError> errors)
        : this(errors, innerException: null)
    {
    }

    internal SqlSchemaValidationException(
        IReadOnlyList<SqlSchemaValidationError> errors,
        Exception? innerException)
        : base(CreateMessage(errors), innerException)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            throw new ArgumentException("At least one schema validation error is required.", nameof(errors));
        }

        Errors = Array.AsReadOnly(errors.ToArray());
    }

    /// <summary>Gets the validation errors.</summary>
    public IReadOnlyList<SqlSchemaValidationError> Errors { get; }

    private static string CreateMessage(IReadOnlyList<SqlSchemaValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return errors.Count == 0
            ? "Database schema validation failed."
            : $"Database schema validation failed for '{errors[0].Declaration}': {errors[0].Message}";
    }
}
