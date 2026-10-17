using System;

namespace Assimalign.Cohesion.Database;

/// <summary>An immutable observation of a database's current provisioning step.</summary>
/// <remarks>Operation and object names belong to the model; the root carries no schema vocabulary.</remarks>
public sealed class DatabaseProvisioningProgress
{
    /// <summary>Initializes a provisioning observation.</summary>
    /// <param name="operation">The model's operation kind.</param>
    /// <param name="objectName">The object being provisioned.</param>
    /// <param name="position">The one-based step position.</param>
    /// <param name="stepCount">The number of steps in the migration.</param>
    /// <param name="isRunning">Whether execution has begun.</param>
    /// <param name="rowsProcessed">Rows processed, or null when unavailable.</param>
    /// <param name="totalRows">Rows to process, or null until counted.</param>
    /// <param name="failureCode">The model's failure code, or null while pending or running.</param>
    /// <exception cref="ArgumentException">An identity is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A position or count is invalid.</exception>
    public DatabaseProvisioningProgress(string operation, string objectName, int position, int stepCount,
        bool isRunning, long? rowsProcessed = null, long? totalRows = null, string? failureCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(position);
        ArgumentOutOfRangeException.ThrowIfLessThan(stepCount, position);
        if (rowsProcessed < 0 || totalRows < 0 || rowsProcessed > totalRows)
        {
            throw new ArgumentOutOfRangeException(nameof(rowsProcessed));
        }
        Operation = operation;
        ObjectName = objectName;
        Position = position;
        StepCount = stepCount;
        IsRunning = isRunning;
        RowsProcessed = rowsProcessed;
        TotalRows = totalRows;
        FailureCode = failureCode;
    }

    /// <summary>Gets the model's operation kind.</summary>
    public string Operation { get; }
    /// <summary>Gets the object being provisioned.</summary>
    public string ObjectName { get; }
    /// <summary>Gets the one-based step position.</summary>
    public int Position { get; }
    /// <summary>Gets the total step count.</summary>
    public int StepCount { get; }
    /// <summary>Gets whether the step is running; otherwise it is pending.</summary>
    public bool IsRunning { get; }
    /// <summary>Gets the number of processed rows, or null when unavailable.</summary>
    public long? RowsProcessed { get; }
    /// <summary>Gets the number of rows to process, or null until counted.</summary>
    public long? TotalRows { get; }
    /// <summary>Gets the failure code, or null while the step has not failed.</summary>
    public string? FailureCode { get; }
}
