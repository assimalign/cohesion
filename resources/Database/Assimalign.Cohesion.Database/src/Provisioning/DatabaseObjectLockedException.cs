using System;

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
    public DatabaseObjectLockedException(string objectName, string owningSchema, string operation)
        : base($"Object '{objectName}' is owned by schema '{owningSchema}' and cannot be changed by {operation}. Alter the schema and redeploy it.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(owningSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
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
