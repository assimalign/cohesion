using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSpilledResultSet
{
    [System.Diagnostics.DebuggerDisplay("SQL spill rows")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(Rows.DebugView))]
    private sealed class Rows : IAsyncEnumerable<DatabaseRow>
    {
        private readonly SqlSpilledResultSet _owner;
        private readonly CancellationToken _token;
        internal Rows(SqlSpilledResultSet owner, CancellationToken token) { _owner = owner; _token = token; }
        public IAsyncEnumerator<DatabaseRow> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new RowEnumerator(_owner, _token, cancellationToken);
        private sealed class DebugView { public DebugView(Rows target) { } }
    }

    [System.Diagnostics.DebuggerDisplay("SQL spill row enumerator")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(RowEnumerator.DebugView))]
    private sealed class RowEnumerator : IAsyncEnumerator<DatabaseRow>
    {
        private readonly SqlSpilledResultSet _owner;
        private readonly CancellationToken _token;
        private readonly CancellationTokenSource? _linked;
        private Cursor? _cursor;
        private long _skip;
        private long _left;
        private bool _complete;
        internal RowEnumerator(SqlSpilledResultSet owner, CancellationToken token, CancellationToken enumerationToken)
        {
            _owner = owner; _skip = owner._offset; _left = owner._limit;
            if (!token.CanBeCanceled) { _token = enumerationToken; }
            else if (!enumerationToken.CanBeCanceled || token == enumerationToken) { _token = token; }
            else { _linked = CancellationTokenSource.CreateLinkedTokenSource(token, enumerationToken); _token = _linked.Token; }
        }
        public DatabaseRow Current => _cursor?.Current!;
        public ValueTask<bool> MoveNextAsync()
        {
            lock (_owner._gate)
            {
                if (_complete) { return new(false); }
                try
                {
                    _cursor ??= _owner.Open(_token);
                    while (_left != 0 && _owner.Read(_cursor, _token, out var row))
                    {
                        if (_skip > 0) { _skip--; continue; }
                        if (_left > 0) { _left--; }
                        // Keep decode and Current publication under the same gate. An async
                        // iterator would retain its old Current until publishing outside Read.
                        SqlRowCollector.ObserveSpill?.Invoke("reader-publish");
                        _cursor.Current = new SqlMaterializedRow(row!);
                        return new(true);
                    }
                    Close(); return new(false);
                }
                catch (Exception exception)
                {
                    try { Close(); }
                    catch (Exception cleanup) when (cleanup is not OutOfMemoryException)
                    { exception.Data[nameof(RowEnumerator)] = cleanup; }
                    return ValueTask.FromException<bool>(exception);
                }
            }
        }
        public ValueTask DisposeAsync()
        {
            lock (_owner._gate)
            {
                try { Close(); return default; }
                catch (Exception exception) { return ValueTask.FromException(exception); }
            }
        }
        private void Close()
        {
            if (_complete) { return; }
            _complete = true;
            try
            {
                if (_cursor is { } cursor)
                {
                    _cursor = null;
                    if (_owner._readers.Remove(cursor)) { _owner._readerBytes -= cursor.ReservedBytes; }
                    cursor.Dispose();
                }
            }
            finally { _linked?.Dispose(); }
        }
        private sealed class DebugView
        {
            private readonly RowEnumerator _target;
            public DebugView(RowEnumerator target) { _target = target; }
            public DatabaseRow Current => _target.Current;
        }
    }
}
