namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Represents the outcome status of executing a request.
/// </summary>
public enum QueryResultStatus : byte
{
    /// <summary>The request completed successfully.</summary>
    Success = 0,
    /// <summary>The request failed during execution.</summary>
    Error,
    /// <summary>The request was cancelled before completion.</summary>
    Cancelled,
    /// <summary>The request exceeded its timeout.</summary>
    Timeout
}
