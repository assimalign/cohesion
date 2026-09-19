using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Mapping.Tests;

internal sealed class MappingTestStore : IMappingStore<MappingTestTransaction>
{
    internal Dictionary<string, string?> Rows { get; private set; } = new(StringComparer.Ordinal);
    internal List<EntityChange<int, MappingTestSnapshot>> Changes { get; } = [];
    internal int BeginCount { get; private set; }
    internal int CommitCount { get; private set; }
    internal int RollbackCount { get; private set; }
    internal int DisposeCount { get; private set; }
    internal int? FailOnWrite { get; set; }
    internal bool FailCommit { get; set; }
    internal bool FailDispose { get; set; }
    internal Action<int>? AfterWrite { get; set; }
    internal Action? AfterCommit { get; set; }
    internal Func<CancellationToken, ValueTask>? BeforeBegin { get; set; }

    public async ValueTask<MappingTestTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeBegin is not null)
        {
            await BeforeBegin(cancellationToken);
        }

        BeginCount++;
        return new MappingTestTransaction(this, new Dictionary<string, string?>(Rows, StringComparer.Ordinal));
    }

    internal void Commit(Dictionary<string, string?> rows)
    {
        if (FailCommit)
        {
            throw new InvalidOperationException("Injected commit failure.");
        }

        Rows = rows;
        CommitCount++;
        AfterCommit?.Invoke();
    }

    internal void Dispose(bool committed)
    {
        DisposeCount++;
        if (!committed)
        {
            RollbackCount++;
        }

        if (FailDispose)
        {
            throw new InvalidOperationException("Injected disposal failure.");
        }
    }
}

internal sealed class MappingTestTransaction(MappingTestStore store, Dictionary<string, string?> rows) : IMappingTransaction
{
    private int _writes;
    private bool _committed;

    internal void Apply(string key, string? value, bool deleted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _writes++;
        if (store.FailOnWrite == _writes)
        {
            throw new InvalidOperationException("Injected write failure.");
        }

        if (deleted)
        {
            rows.Remove(key);
        }
        else
        {
            rows[key] = value;
        }

        store.AfterWrite?.Invoke(_writes);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Commit(rows);
        _committed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        store.Dispose(_committed);
        return ValueTask.CompletedTask;
    }
}

internal sealed class MappingTestEntity
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public byte[]? Payload { get; set; }
    public List<string> Unmapped { get; } = [];
}

internal readonly record struct MappingTestSnapshot(string? Name, byte[]? Payload);

internal sealed class MappingTestMapper : IEntityMapper<MappingTestEntity, int, MappingTestSnapshot>
{
    internal bool FailCapture { get; set; }

    public int GetKey(MappingTestEntity entity) => entity.Id;

    public MappingTestSnapshot Capture(MappingTestEntity entity)
    {
        if (FailCapture)
        {
            throw new InvalidOperationException("Injected capture failure.");
        }

        return new(entity.Name, entity.Payload is null ? null : (byte[])entity.Payload.Clone());
    }

    public bool AreEqual(MappingTestSnapshot left, MappingTestSnapshot right)
        => left.Name == right.Name
            && (left.Payload is null
                ? right.Payload is null
                : right.Payload is not null && left.Payload.AsSpan().SequenceEqual(right.Payload));
}

internal sealed class MappingTestWriter(MappingTestStore store) : IEntityChangeWriter<int, MappingTestSnapshot, MappingTestTransaction>
{
    public ValueTask ApplyAsync(MappingTestTransaction transaction, EntityChange<int, MappingTestSnapshot> change, CancellationToken cancellationToken = default)
    {
        store.Changes.Add(change);
        transaction.Apply($"entity:{change.Key}", change.Current.Name, change.Kind == EntityChangeKind.Deleted, cancellationToken);
        return ValueTask.CompletedTask;
    }
}

internal sealed class MappingTestNote
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

internal sealed class MappingTestNoteMapper : IEntityMapper<MappingTestNote, string, string>
{
    public string GetKey(MappingTestNote entity) => entity.Id;
    public string Capture(MappingTestNote entity) => entity.Text;
    public bool AreEqual(string left, string right) => StringComparer.Ordinal.Equals(left, right);
}

internal sealed class MappingTestNoteWriter : IEntityChangeWriter<string, string, MappingTestTransaction>
{
    public ValueTask ApplyAsync(MappingTestTransaction transaction, EntityChange<string, string> change, CancellationToken cancellationToken = default)
    {
        transaction.Apply($"note:{change.Key}", change.Current, change.Kind == EntityChangeKind.Deleted, cancellationToken);
        return ValueTask.CompletedTask;
    }
}
