using System;
using System.Threading;

namespace Assimalign.Cohesion.Resilience.Internal;

using Internal;

internal class ResilienceContext : IResilienceContext
{
    public ResilienceContext() { }

    public OperationKey? OperationKey { get; internal set; }
    public CancellationToken CancellationToken { get; internal set; }
    public bool ContinueOnCapturedContext { get; internal set; }
    public IServiceProvider? ServiceProvider { get; internal set; }
}
