using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Schema;

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
