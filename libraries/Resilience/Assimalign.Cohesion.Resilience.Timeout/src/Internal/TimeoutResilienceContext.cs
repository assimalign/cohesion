using System.Threading;

namespace Assimalign.Cohesion.Resilience.Internal;

internal sealed class TimeoutResilienceContext : IResilienceContext
{
    public TimeoutResilienceContext(IResilienceContext context, CancellationToken cancellationToken)
    {
        OperationKey = context.OperationKey;
        ContinueOnCapturedContext = context.ContinueOnCapturedContext;
        CancellationToken = cancellationToken;
    }

    public OperationKey OperationKey { get; }

    public CancellationToken CancellationToken { get; }

    public bool ContinueOnCapturedContext { get; }
}
