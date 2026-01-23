using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class TimeoutResilienceContext : ResilienceContext
{
    private readonly CancellationToken _cancellationToken;

    public TimeoutResilienceContext(CancellationToken cancellationToken, bool continueOnCapturedContext, OperationKey? operationKey)
    {
        _cancellationToken = cancellationToken;
        ContinueOnCapturedContext = continueOnCapturedContext;
        OperationKey = operationKey;
    }

    public override bool ContinueOnCapturedContext { get; }
    public override OperationKey? OperationKey { get; }
    public override CancellationToken CancellationToken =>  _cancellationToken;
}
