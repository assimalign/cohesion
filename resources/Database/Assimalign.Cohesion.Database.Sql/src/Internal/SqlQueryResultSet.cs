using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// Represents a SELECT result set that streams rows from storage.
/// </summary>
internal sealed class SqlQueryResultSet : QueryResultSet
{
    private readonly IStorageUnitIterator _iterator;
    private bool _disposed;

    internal SqlQueryResultSet(IStorageUnitIterator iterator)
    {
        _iterator = iterator;
    }

    /// <inheritdoc />
    public override QueryResultStatus Status => QueryResultStatus.Success;

    /// <inheritdoc />
    public override long AffectedCount => -1;

    /// <inheritdoc />
    public override IReadOnlyList<Diagnostic>? Diagnostics => null;

    /// <inheritdoc />
    public override IReadOnlyList<QueryColumn> Columns { get; } =
    [
        new QueryColumn
        {
            Name = "data",
            Ordinal = 0,
            Type = DatabaseType.Binary,
            IsNullable = false
        }
    ];

    /// <inheritdoc />
    public override async IAsyncEnumerable<QueryRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (_iterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new SqlQueryRow(_iterator.Current.Data);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _iterator.Dispose();
        }

        return default;
    }
}
