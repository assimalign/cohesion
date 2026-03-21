using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents a pipeline failure that aggregated one or more strategy failures.
/// </summary>
public sealed class ResiliencePipelineFailureException : ResilienceException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResiliencePipelineFailureException"/> class.
    /// </summary>
    /// <param name="operationKey">The operation key associated with the failed execution.</param>
    /// <param name="message">The failure message.</param>
    public ResiliencePipelineFailureException(OperationKey operationKey, string message)
        : this(operationKey, message, [])
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResiliencePipelineFailureException"/> class.
    /// </summary>
    /// <param name="operationKey">The operation key associated with the failed execution.</param>
    /// <param name="message">The failure message.</param>
    /// <param name="failures">The strategy failures collected by the pipeline.</param>
    public ResiliencePipelineFailureException(
        OperationKey operationKey,
        string message,
        ResilienceStrategyFailureException[] failures)
        : base(
            ResilienceErrorCode.PipelineFailure,
            operationKey,
            message,
            TryGetAggregateException(failures))
    {
        Failures = failures;
    }

    /// <summary>
    /// Gets the collection of failures that occurred during the execution of the resilience pipeline.
    /// </summary>
    public ResilienceStrategyFailureException[] Failures { get; }

    private static Exception? TryGetAggregateException(ResilienceStrategyFailureException[] failures)
    {
        return failures.Length == 0
            ? null
            : new AggregateException(failures);
    }
}
