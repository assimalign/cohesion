using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Thrown when a statement or command on a live session attempts to alter or drop an object
/// provisioned from a compiled schema. This separates code-first provisioning from ad-hoc
/// statements; objects created by ad-hoc statements remain fully mutable by those statements.
/// </summary>
public sealed class DatabaseObjectLockedException : DatabaseException
{
    /// <summary>Initializes a refusal to change an object owned by a compiled schema.</summary>
    /// <param name="objectName">The object the operation targeted.</param>
    /// <param name="owningSchema">The compiled schema that provisioned the object.</param>
    /// <param name="operation">The refused operation in the model's own vocabulary.</param>
    /// <param name="diagnostic">Optional diagnostic metadata; an absent message or severity uses the refusal's message or Error.</param>
    /// <exception cref="ArgumentException">An object name, schema name or operation is null, empty or whitespace.</exception>
    public DatabaseObjectLockedException(string objectName, string owningSchema, string operation, Diagnostic? diagnostic = null)
        : this(objectName, owningSchema, operation,
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.ObjectOwnedBySchema, objectName, owningSchema, operation), diagnostic)
    {
    }

    /// <summary>
    /// Initializes a refusal whose owner is not a schema object's compiled schema, with a message
    /// naming that owner and its remedy: a SQL engine's declared database, which its declaration
    /// owns at the database level (owner decision 56 of 2026-10-09).
    /// </summary>
    /// <param name="objectName">The object the operation targeted.</param>
    /// <param name="owningSchema">The declaration that owns the object.</param>
    /// <param name="operation">The refused operation in the model's own vocabulary.</param>
    /// <param name="message">The message, which names the owner and the remedy.</param>
    /// <param name="diagnostic">Optional diagnostic metadata; an absent message or severity uses the refusal's message or Error.</param>
    /// <exception cref="ArgumentException">An object name, schema name, operation or message is null, empty or whitespace.</exception>
    public DatabaseObjectLockedException(string objectName, string owningSchema, string operation, string message, Diagnostic? diagnostic = null)
        : base(message, diagnostic is null ? [] : [new Diagnostic
        {
            Code = diagnostic.Code,
            Message = diagnostic.Message ?? message,
            Severity = diagnostic.Severity ?? DiagnosticSeverity.Error,
            Location = diagnostic.Location,
            Start = diagnostic.Start,
            End = diagnostic.End,
            Line = diagnostic.Line,
        }])
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(owningSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ObjectName = objectName;
        OwningSchema = owningSchema;
        Operation = operation;
    }

    /// <summary>The object the operation targeted.</summary>
    public string ObjectName { get; }

    /// <summary>The compiled schema that provisioned the object.</summary>
    public string OwningSchema { get; }

    /// <summary>The refused operation, in the model's own vocabulary (e.g. "DROP TABLE").</summary>
    public string Operation { get; }
}
