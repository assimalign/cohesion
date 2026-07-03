using System;
using System.Threading;

namespace Assimalign.Cohesion.Hosting.Tests;

internal sealed class DelegateDedicatedThreadService : DedicatedThreadService
{
    private readonly Action<CancellationToken> _run;

    public DelegateDedicatedThreadService(Action<CancellationToken> run)
    {
        _run = run;
    }

    protected override void Run(CancellationToken cancellationToken)
    {
        _run(cancellationToken);
    }
}
