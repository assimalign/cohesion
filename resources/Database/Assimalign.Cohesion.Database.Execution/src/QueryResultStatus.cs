namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Represents the outcome status of a query execution.
/// </summary>
public enum QueryResultStatus : byte
{
    /// <summary>The query completed successfully.</summary>
    Success = 0,
    /// <summary>The query encountered an error during execution.</summary>
    Error,
    /// <summary>The query was cancelled before completion.</summary>
    Cancelled,
    /// <summary>The query exceeded its timeout.</summary>
    Timeout
}
