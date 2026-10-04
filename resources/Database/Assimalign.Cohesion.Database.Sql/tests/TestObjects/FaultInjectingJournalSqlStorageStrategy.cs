using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// An in-memory SQL storage strategy whose journals fail writes on demand. Writes fail only on
/// the asynchronous flow that armed the failure, so a test can fail one journal append of its own
/// call while the engine's background workers keep writing normally.
/// </summary>
internal sealed class FaultInjectingJournalSqlStorageStrategy : ISqlStorageStrategy
{
    private static readonly AsyncLocal<StrongBox<int>?> s_failures = new();
    private readonly HashSet<string> _storages = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    /// <summary>
    /// Fails the next <paramref name="writes"/> journal writes made on the calling flow, until the
    /// returned scope is disposed.
    /// </summary>
    /// <param name="writes">The number of writes to fail.</param>
    /// <returns>The scope that disarms the failure and reports how many failures remain unspent.</returns>
    internal static FailureScope FailJournalWrites(int writes)
    {
        var previous = s_failures.Value;
        var budget = new StrongBox<int>(writes);
        s_failures.Value = budget;
        return new FailureScope(previous, budget);
    }

    /// <inheritdoc />
    public SqlStorage CreateStorage(string databaseName)
    {
        lock (_sync)
        {
            if (!_storages.Add(databaseName))
            {
                throw new DatabaseException($"Fault-injecting storage for '{databaseName}' already exists.");
            }
        }

        return SqlStorage.Create(StorageStream.FromInMemory(), new StorageStream(new FaultInjectingStream()),
            StorageStream.FromInMemory(), databaseName);
    }

    /// <inheritdoc />
    public SqlStorage OpenStorage(string databaseName)
        => throw new NotSupportedException("In-memory fault-injecting storage cannot be reopened.");

    /// <inheritdoc />
    public void DropStorage(string databaseName)
    {
        lock (_sync)
        {
            _storages.Remove(databaseName);
        }
    }

    /// <inheritdoc />
    public bool StorageExists(string databaseName)
    {
        lock (_sync)
        {
            return _storages.Contains(databaseName);
        }
    }

    /// <summary>The armed failure budget of one calling flow.</summary>
    internal sealed class FailureScope : IDisposable
    {
        private readonly StrongBox<int>? _previous;
        private readonly StrongBox<int> _budget;

        /// <summary>Initializes a new instance of the <see cref="FailureScope"/> class.</summary>
        /// <param name="previous">The failure budget the scope replaced, restored on disposal.</param>
        /// <param name="budget">The failure budget the scope armed.</param>
        public FailureScope(StrongBox<int>? previous, StrongBox<int> budget)
        {
            _previous = previous;
            _budget = budget;
        }

        /// <summary>Gets the number of armed failures no write has spent yet.</summary>
        public int Remaining => _budget.Value;

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
