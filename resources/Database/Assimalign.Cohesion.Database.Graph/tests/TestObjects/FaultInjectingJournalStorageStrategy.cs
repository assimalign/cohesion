using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// An in-memory graph storage strategy whose journal fails writes on demand. Writes fail only on
/// the asynchronous flow that armed the failure, so a test can fail one journal append of its own
/// call while the engine's background workers keep writing normally.
/// </summary>
internal sealed class FaultInjectingJournalStorageStrategy : IGraphStorageStrategy
{
    private static readonly AsyncLocal<StrongBox<int>?> s_failures = new();
    private readonly HashSet<string> _databases = new(StringComparer.Ordinal);

    /// <summary>
    /// Fails the next <paramref name="writes"/> journal writes made on the calling flow, until the
    /// returned scope is disposed.
    /// </summary>
    /// <param name="writes">The number of writes to fail.</param>
    /// <returns>The scope that disarms the failure.</returns>
    internal static IDisposable FailJournalWrites(int writes)
    {
        var previous = s_failures.Value;
        s_failures.Value = new StrongBox<int>(writes);
        return new Scope(previous);
    }

    public GraphStorage CreateStorage(DatabaseName databaseName, StorageCommitDurability? durability)
    {
        _databases.Add(databaseName.ToString());
        return GraphStorage.Create(StorageStream.FromInMemory(), new StorageStream(new FaultInjectingStream()),
            StorageStream.FromInMemory(), databaseName, durability);
    }

    public GraphStorage OpenStorage(DatabaseName databaseName, StorageCommitDurability? durability)
        => throw new NotSupportedException("In-memory fault-injecting storage cannot be reopened.");

    public void DropStorage(DatabaseName databaseName) => _databases.Remove(databaseName.ToString());

    public bool StorageExists(DatabaseName databaseName) => _databases.Contains(databaseName.ToString());

    public IEnumerable<DatabaseName> GetDatabaseNames()
    {
        foreach (string name in _databases)
        {
            yield return new DatabaseName(name);
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly StrongBox<int>? _previous;

        /// <summary>Initializes a new instance of the <see cref="Scope"/> class.</summary>
        /// <param name="previous">The failure budget the scope replaced, restored on disposal.</param>
        public Scope(StrongBox<int>? previous)
        {
            _previous = previous;
        }

        public void Dispose() => s_failures.Value = _previous;
    }

    private sealed class FaultInjectingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfArmed();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfArmed();
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            ThrowIfArmed();
            base.WriteByte(value);
        }

        private static void ThrowIfArmed()
        {
            if (s_failures.Value is { Value: > 0 } failures)
            {
                failures.Value--;
                throw new IOException("Injected journal write failure.");
            }
        }
    }
}
