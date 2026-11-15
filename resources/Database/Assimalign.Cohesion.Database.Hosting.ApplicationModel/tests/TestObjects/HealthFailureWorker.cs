using System;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

internal sealed class HealthFailureWorker : DatabaseEngineWorker
{
    internal HealthFailureWorker() : base("health/probe", DatabaseEngineWorkerKind.Checkpoint, TimeSpan.FromHours(1)) { }

    internal int Passes { get; private set; }

    protected override void WaitForTrigger(CancellationToken cancellationToken)
        => cancellationToken.WaitHandle.WaitOne();

    protected override void RunIterationCore(CancellationToken cancellationToken)
    {
        if (BeginDatabase("offline"))
        {
            ReportFailure("offline", new IOException("private storage path must not escape"), TimeSpan.Zero);
        }
        Passes++;
    }
}
