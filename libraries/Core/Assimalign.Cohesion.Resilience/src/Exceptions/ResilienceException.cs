using System;

namespace Assimalign.Cohesion.Resilience;


public enum ResilienceErrorCode
{
    /// <summary>
    /// Exception thrown when a policy rejects execution of a delegate.
    /// <remarks>More specific exceptions which derive from this type, are generally thrown.</remarks>
    /// </summary>
    ExecutionRejected,

    /// <summary>
    /// 
    /// </summary>
    PipelineFailure,

    /// <summary>
    /// 
    /// </summary>
    StrategyFailure
}

public class ResilienceException : Exception
{
    public ResilienceException(ResilienceErrorCode code, string message) 
        : base(message)
    {
        Code = code;
    }

    public ResilienceException(ResilienceErrorCode code, string message, Exception? innerException) 
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>
    /// 
    /// </summary>
    public ResilienceErrorCode Code { get; }
}

