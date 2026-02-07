using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class TimeoutResilienceContext : IResilienceContext
{
    private readonly CancellationToken _cancellationToken;

    public TimeoutResilienceContext(CancellationToken cancellationToken, bool continueOnCapturedContext, OperationKey? operationKey)
    {
        _cancellationToken = cancellationToken;
        ContinueOnCapturedContext = continueOnCapturedContext;
        OperationKey = operationKey;
    }

    public bool ContinueOnCapturedContext { get; }
    public OperationKey? OperationKey { get; }
    public CancellationToken CancellationToken =>  _cancellationToken;
    public IServiceProvider? ServiceProvider => throw new NotImplementedException();
}
