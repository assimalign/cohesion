using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Tests;

internal sealed class DelegateBackgroundService : BackgroundService
{
    private readonly Func<CancellationToken, Task> _execute;

    public DelegateBackgroundService(Func<CancellationToken, Task> execute)
    {
        _execute = execute;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return _execute(cancellationToken);
    }
}
