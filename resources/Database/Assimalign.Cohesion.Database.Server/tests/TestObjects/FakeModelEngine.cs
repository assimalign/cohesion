using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Server.Tests;

/// <summary>
/// A minimal fake model engine: one database ("app") whose sessions execute a
/// canned statement vocabulary through the root's text-execute seam. The shared
/// server core is proven model-independent precisely because this suite drives
/// it with no real model at all.
/// </summary>
/// <remarks>
/// Canned statements: <c>rows</c> streams a two-row result set; <c>echo</c>
/// returns the bound <c>@value</c> parameter back as one row; <c>count</c>
/// returns a plain result with affected count 3; <c>parse-error</c> throws the
/// root's <see cref="DatabaseParseException"/>; <c>exec-error</c> throws a
/// <see cref="DatabaseException"/>; anything else succeeds with affected count 0.
/// </remarks>
internal sealed class FakeModelEngine : IDatabaseEngine
{
    private readonly FakeDatabase _database;
    private bool _disposed;

    internal FakeModelEngine(string databaseName = "app")
    {
        _database = new FakeDatabase(databaseName, this);
    }

    public string Name => "fake-engine";

    public EngineModel Model => EngineModel.Custom;

    public EngineState State => _disposed ? EngineState.Disposed : EngineState.Running;

    public IReadOnlyList<IDatabaseEngineWorker> Workers => Array.Empty<IDatabaseEngineWorker>();

    public ValueTask<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
        => throw new DatabaseException("The fake engine holds a fixed database set.");

    public ValueTask<IDatabase> OpenDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        if (TryGetDatabase(name, out var database))
        {
            return new ValueTask<IDatabase>(database);
        }

        throw new DatabaseException($"Database '{name}' does not exist.");
    }

    public ValueTask DropDatabaseAsync(string name, CancellationToken cancellationToken = default)
        => throw new DatabaseException("The fake engine holds a fixed database set.");

    public async IAsyncEnumerable<IDatabase> GetDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return _database;
        await Task.CompletedTask;
    }

    public bool TryGetDatabase(string name, out IDatabase database)
    {
        if (string.Equals(name, (string)_database.Name, StringComparison.OrdinalIgnoreCase))
        {
            database = _database;
            return true;
        }

        database = null!;
        return false;
    }

    public void Dispose() => _disposed = true;

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private sealed class FakeDatabase : IDatabase
    {
        internal FakeDatabase(string name, IDatabaseEngine engine)
        {
            Name = name;
            Engine = engine;
        }

        public DatabaseName Name { get; }

        public IDatabaseEngine Engine { get; }

        public ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
            => new(new FakeSession(this));

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSession : IDatabaseSession
    {
        internal FakeSession(IDatabase database) => Database = database;

        public IDatabase Database { get; }

        public SessionState State => SessionState.Open;

        public IDatabaseTransaction? CurrentTransaction => null;

        public ValueTask<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            => throw new DatabaseException("The fake session does not support transactions.");

        public ValueTask<IDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
            => throw new DatabaseException("The fake session does not support transactions.");

        public ValueTask<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => throw new DatabaseException("The fake session executes text only.");

        public ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
        {
            return statement switch
            {
                "rows" => new ValueTask<QueryResult>(new FakeResultSet(
                    [
                        new QueryColumn { Name = "name", Ordinal = 0, Type = DatabaseType.String },
                        new QueryColumn { Name = "value", Ordinal = 1, Type = DatabaseType.Int32 },
                    ],
                    [["a", 1], ["b", 2]])),
                "echo" => new ValueTask<QueryResult>(new FakeResultSet(
                    [new QueryColumn { Name = "echo", Ordinal = 0, Type = DatabaseType.Int64 }],
                    [[parameters?["value"]]])),
                "count" => new ValueTask<QueryResult>(new FakeResult(QueryResultStatus.Success, 3)),
                "parse-error" => throw new DatabaseParseException("The fake language rejects this statement."),
                "exec-error" => throw new DatabaseException("The fake executor failed this statement."),
                _ => new ValueTask<QueryResult>(new FakeResult(QueryResultStatus.Success, 0)),
            };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeResult : QueryResult
    {
        internal FakeResult(QueryResultStatus status, long affectedCount)
        {
            Status = status;
            AffectedCount = affectedCount;
        }

        public override QueryResultStatus Status { get; }

        public override long AffectedCount { get; }

        public override IReadOnlyList<Diagnostic>? Diagnostics => null;
    }

    private sealed class FakeResultSet : QueryResultSet
    {
        private readonly List<object?[]> _rows;

        internal FakeResultSet(IReadOnlyList<QueryColumn> columns, List<object?[]> rows)
        {
            Columns = columns;
            _rows = rows;
        }

        public override QueryResultStatus Status => QueryResultStatus.Success;

        public override long AffectedCount => -1;

        public override IReadOnlyList<Diagnostic>? Diagnostics => null;

        public override IReadOnlyList<QueryColumn> Columns { get; }

        public override async IAsyncEnumerable<QueryRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var row in _rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new FakeRow(row);
            }

            await Task.CompletedTask;
        }

        public override ValueTask DisposeAsync() => default;
    }

    private sealed class FakeRow : QueryRow
    {
        private readonly object?[] _values;

        internal FakeRow(object?[] values) => _values = values;

        public override int FieldCount => _values.Length;

        public override bool IsNull(int ordinal) => _values[ordinal] is null;

        public override ReadOnlyMemory<byte> GetBytes(int ordinal) => (byte[])_values[ordinal]!;

        public override string? GetString(int ordinal) => _values[ordinal]?.ToString();

        public override int GetInt32(int ordinal) => (int)_values[ordinal]!;

        public override long GetInt64(int ordinal) => (long)_values[ordinal]!;

        public override bool GetBoolean(int ordinal) => (bool)_values[ordinal]!;

        public override double GetDouble(int ordinal) => (double)_values[ordinal]!;

        public override object? GetValue(int ordinal) => _values[ordinal];
    }
}
