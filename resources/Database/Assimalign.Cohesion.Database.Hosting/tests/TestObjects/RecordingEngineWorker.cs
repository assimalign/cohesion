using System;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

internal sealed class RecordingEngineWorker : IDatabaseEngineWorker
{
    internal RecordingEngineWorker(
        string name,
        DatabaseEngineWorkerKind kind,
        TimeSpan interval)
    {
        Name = name;
        Kind = kind;
        Interval = interval;
    }

    public string Name { get; }

    public DatabaseEngineWorkerKind Kind { get; }

    public TimeSpan Interval { get; }
}
