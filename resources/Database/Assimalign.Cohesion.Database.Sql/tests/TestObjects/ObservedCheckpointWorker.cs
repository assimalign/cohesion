using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class ObservedCheckpointWorker : DatabaseCheckpointWorker<ObservedCheckpointWorker.Database>, IDisposable
{
    private readonly ManualResetEventSlim _signal = new();

    internal ObservedCheckpointWorker() : base("observed", TimeSpan.FromHours(1), long.MaxValue) { }

    internal Database[] Databases { get; set; } = [new Database()];
    internal Func<bool> Work { get; set; } = static () => true;
    internal bool FailSnapshot { get; set; }

    protected override ManualResetEventSlim CheckpointNeededSignal => _signal;
    protected override Database[] GetDatabases() => FailSnapshot ? throw new InvalidOperationException("snapshot failed") : Databases;
    protected override string GetName(Database database) => database.Name;
    protected override bool IsOffline(Database database) => database.Offline;
    protected override bool IsOpen(Database database) => database.Open;
    protected override long GetJournalLength(Database database) => 0;
    protected override bool IsCheckpointDue(Database database, TimeSpan interval, DatabaseWorkerPass.Visit? visit)
    {
        if (visit is not null) { visit.Due = new(false, database.Due, false, 0, 0); }
        return database.Due;
    }
    protected override bool Checkpoint(Database database, TimeSpan interval, CancellationToken cancellationToken) => Work();

    public void Dispose()
    {
        DisposeAsyncCore().GetAwaiter().GetResult();
        _signal.Dispose();
    }

    internal sealed class Database
    {
        internal string Name { get; set; } = "database";
        internal bool Offline { get; set; }
        internal bool Open { get; set; } = true;
        internal bool Due { get; set; } = true;
    }
}
