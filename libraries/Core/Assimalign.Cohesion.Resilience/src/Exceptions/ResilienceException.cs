using System;

namespace Assimalign.Cohesion.Resilience;

using Properties;

/// <summary>
/// 
/// </summary>
public class ResilienceException : Exception
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="code"></param>
    /// <param name="message"></param>
    /// <param name="innerException"></param>
    public ResilienceException(ResilienceErrorCode code, OperationKey operationKey, string message) 
        : base(message)
    {
        Code = code;
        OperationKey = operationKey;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="code"></param>
    /// <param name="message"></param>
    /// <param name="operationKey"></param>
    /// <param name="innerException"></param>
    public ResilienceException(ResilienceErrorCode code, OperationKey operationKey, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
        OperationKey = operationKey;
    }

    /// <summary>
    /// 
    /// </summary>
    public OperationKey OperationKey { get; }

    /// <summary>
    /// 
    /// </summary>
    public virtual ResilienceErrorCode Code { get; }


    public static ResilienceException PipelineFailure(OperationKey operationKey = default, Exception? innerException = default)
    {
        return new ResilienceException(
            code: ResilienceErrorCode.PipelineFailure,
            operationKey: operationKey,
            message: ErrorMessages.PipelineFailure,
            innerException: innerException);
    }

    public static ResilienceException StrategyFailure(OperationKey operationKey = default, Exception? innerException = default)
    {
        return new ResilienceException(
            code: ResilienceErrorCode.StrategyFailure,
            message: "",
            operationKey: operationKey,
            innerException: innerException);
    }
}


public sealed class 