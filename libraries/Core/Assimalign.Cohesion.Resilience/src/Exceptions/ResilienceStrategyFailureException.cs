using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Resilience;

public class ResilienceStrategyFailureException : ResilienceException
{
    public ResilienceStrategyFailureException(OperationKey operationKey, string message)
        : this(operationKey, message, null)
    {

    }

    public ResilienceStrategyFailureException(OperationKey operationKey, string message, Exception? reason)
        : base(message, reason)
    {
        OperationKey = operationKey;
    }

    /// <inheritdoc />
    public override OperationKey OperationKey { get; }

    /// <inheritdoc />
    public override ResilienceErrorCode Code { get; } = ResilienceErrorCode.StrategyFailure;
}
