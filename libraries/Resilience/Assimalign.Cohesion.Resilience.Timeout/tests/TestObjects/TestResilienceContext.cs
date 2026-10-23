using System.Threading;

namespace Assimalign.Cohesion.Resilience.Timeout.Tests.TestObjects;

internal sealed class TestResilienceContext : IResilienceContext
{
    public TestResilienceContext(CancellationToken cancellationToken = default)
    {
        CancellationToken = cancellationToken;
    }

    public OperationKey OperationKey => default;

    public CancellationToken CancellationToken { get; }

    public bool ContinueOnCapturedContext => false;
}
