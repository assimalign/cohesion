namespace Assimalign.Cohesion.Resilience;

public enum ResilienceErrorCode
{
    /// <summary>
    /// Exception thrown when a policy rejects execution of a delegate.
    /// <remarks>More specific exceptions which derive from this type, are generally thrown.</remarks>
    /// </summary>
    ExecutionRejected,

    /// <summary>
    /// Exception thrown when strategy was completed in full but failure still occurred.
    /// </summary>
    StrategyFailure,

    /// <summary>
    /// An exception is thrown when the entire pipeline was executed without any strategy firing successfully.
    /// </summary>
    PipelineFailure,
}
