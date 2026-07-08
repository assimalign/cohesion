using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Health;

namespace Assimalign.Cohesion.Health.Hosting.Tests;

/// <summary>
/// An <see cref="IHealthPublisher"/> test double that records every report it receives and
/// signals when the first one arrives.
/// </summary>
internal sealed class RecordingPublisher : IHealthPublisher
{
    private readonly TaskCompletionSource _firstReport = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HealthReport? LastReport { get; private set; }

    public int PublishCount { get; private set; }

    public ValueTask PublishAsync(HealthReport report, CancellationToken cancellationToken = default)
    {
        LastReport = report;
        PublishCount++;
        _firstReport.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public Task WaitForFirstReportAsync(TimeSpan timeout) => _firstReport.Task.WaitAsync(timeout);
}
