using System;

namespace Assimalign.Cohesion.Resilience;

public sealed class ResiliencePipelineFailureException : ResilienceException
{
    public ResiliencePipelineFailureException(OperationKey operationKey, string message) 
        : this(operationKey, message, [])
    {

    }

    public ResiliencePipelineFailureException(OperationKey operationKey, string message, ResilienceStrategyFailureException[] failures) 
        : base(message, TryGetAggregateException(failures))
    {
        OperationKey = operationKey;
        Failures = failures;
    }

    /// <inheritdoc />
    public override OperationKey OperationKey { get; }

    /// <inheritdoc />
    public override ResilienceErrorCode Code { get; } = ResilienceErrorCode.PipelineFailure;

    /// <summary>
    /// Gets the collection of failures that occurred during the execution of the resilience pipeline.
    /// </summary>
    /// <remarks>Use this property to inspect the individual exceptions that were encountered while executing
    /// the resilience pipeline. Each element in the array represents a distinct failure, which can assist in diagnosing
    /// issues or understanding the sequence of errors that led to the current state.</remarks>
    public ResilienceStrategyFailureException[] Failures { get; }



    private static Exception? TryGetAggregateException(ResilienceStrategyFailureException[] failures)
    {
        if (failures.Length == 0)
        {
            return null;
        }

        return new AggregateException(failures);
    }
}
