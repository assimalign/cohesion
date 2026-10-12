using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Prevents session DDL from interleaving with a complete schema apply. Admission fails
/// immediately instead of waiting outside the transaction lock manager's deadlock graph.
/// Ordinary DDL statements may still run concurrently with one another.
/// </summary>
internal sealed class SqlDdlInterlock
{
    private readonly object _sync = new();
    private string? _schema;
    private int _statements;

    internal IDisposable EnterApply(string schema)
    {
        lock (_sync)
        {
            if (_schema is not null || _statements != 0)
            {
                throw new DatabaseObjectLockedException(schema, _schema ?? schema, "APPLY SCHEMA");
            }
            _schema = schema;
            return new Lease(this, apply: true);
        }
    }

    internal IDisposable EnterStatement(string database)
    {
        lock (_sync)
        {
            if (_schema is not null)
            {
                throw new DatabaseObjectLockedException(database, _schema, "DDL during APPLY SCHEMA");
            }
            _statements++;
            return new Lease(this, apply: false);
        }
    }

    private sealed class Lease : IDisposable
    {
        private SqlDdlInterlock? _owner;
        private readonly bool _apply;

        internal Lease(SqlDdlInterlock owner, bool apply)
        {
            _owner = owner;
            _apply = apply;
        }

        public void Dispose()
        {
            if (_owner is not { } owner)
            {
                return;
            }
            lock (owner._sync)
            {
                if (_apply)
                {
                    owner._schema = null;
                }
                else
                {
                    owner._statements--;
                }
                _owner = null;
            }
        }
    }
}
