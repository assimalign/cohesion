using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A guided worker whose passes run a script given the pass number, and which exposes the base's
/// per-database reporting to that script.
/// </summary>
internal sealed class ScriptedWorker : DatabaseEngineWorker
{
    private readonly Action<ScriptedWorker, int> _pass;
    private int _passes;
    private int _failTriggers;

    public ScriptedWorker(
        Action<ScriptedWorker, int> pass,
        TimeSpan? interval = null,
        string name = "scripted",
        DatabaseEngineWorkerKind kind = DatabaseEngineWorkerKind.Checkpoint)
        : base(name, kind, interval ?? TimeSpan.FromHours(1))
    {
        _pass = pass;
    }

    /// <summary>Gets or sets the number of upcoming trigger waits that throw.</summary>
    public int FailTriggers
    {
        get => Volatile.Read(ref _failTriggers);
        init => _failTriggers = value;
    }

    /// <summary>Gets the number of passes run so far.</summary>
    public int Passes => Volatile.Read(ref _passes);

    public bool Begin(string database) => BeginDatabase(database);

    public void Fail(string database, Exception exception) => ReportFailure(database, exception);

    public void Fail(string database, Exception exception, TimeSpan retryAfter) => ReportFailure(database, exception, retryAfter);

    public void Unfinished(string database) => ReportUnfinished(database);

    protected override void RunIterationCore(CancellationToken cancellationToken)
        => _pass(this, Interlocked.Increment(ref _passes));

    protected override void WaitForTrigger(CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref _failTriggers) >= 0)
        {
            throw new InvalidOperationException("trigger failed");
        }

        base.WaitForTrigger(cancellationToken);
    }
}
