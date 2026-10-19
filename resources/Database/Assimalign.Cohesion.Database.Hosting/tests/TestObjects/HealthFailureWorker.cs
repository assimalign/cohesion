using System;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

internal sealed class HealthFailureWorker : DatabaseEngineWorker
{
    private string[] _names = [];

    internal HealthFailureWorker()
        : base("health-review/probe", DatabaseEngineWorkerKind.Checkpoint, TimeSpan.FromHours(1))
    {
    }

    internal void Fail(params string[] names)
    {
        _names = names;
        RunIteration(CancellationToken.None);
        Thread.Sleep(1);
        RunIteration(CancellationToken.None);
        _names = [];
    }

    protected override void RunIterationCore(CancellationToken cancellationToken)
    {
        foreach (string name in _names)
        {
            if (BeginDatabase(name)) { ReportFailure(name, new IOException("health review probe"), TimeSpan.Zero); }
        }
    }
}
