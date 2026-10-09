using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The database's bound table versions: every persisted CHECK and DEFAULT of a table version is
/// parsed and bound exactly once, and validated writes evaluate the cached result.
/// </summary>
/// <remarks>
/// <para>
/// The key is the <see cref="SqlCatalogTable"/> instance itself. Catalog table descriptions are
/// immutable, and every DDL that changes a table's columns or constraints publishes a new
/// instance — <c>ALTER TABLE ADD/DROP CONSTRAINT</c>, <c>ADD/DROP COLUMN</c>, and <c>DROP</c> then
/// <c>CREATE</c> of the same name — so a changed definition is a new key and can never be served
/// a stale binding. Entries for replaced versions are collected with them.
/// </para>
/// <para>
/// The database binds every table when it opens (<see cref="BindCatalog"/>), so a persisted
/// definition that does not load fails the open, naming its table and constraint or column, and
/// never a later write. Each DDL binds the version it publishes before the statement completes.
/// A version that reaches a statement unbound, such as a table a test created through the
/// catalog directly, is bound on first use.
/// </para>
/// </remarks>
internal sealed class SqlBoundTableCache
{
    private readonly SqlCatalog _catalog;
    private readonly ConditionalWeakTable<SqlCatalogTable, SqlBoundTable> _bound = new();
    private readonly object _sync = new();
    private long _bindCount;

    /// <summary>Initializes an empty cache over a catalog.</summary>
    /// <param name="catalog">The catalog whose table versions are bound.</param>
    internal SqlBoundTableCache(SqlCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>
    /// Gets how many table versions this cache has bound. Binding is the only place the engine
    /// parses persisted catalog text, which lets tests prove the write path parses nothing.
    /// </summary>
    internal long BindCount => Interlocked.Read(ref _bindCount);

    /// <summary>Gets the bound form of a table version, binding it on first use.</summary>
    /// <param name="table">The table version.</param>
    /// <returns>The bound table version.</returns>
    /// <exception cref="DatabaseException">A persisted definition of the table does not load.</exception>
    /// <exception cref="InsufficientExecutionStackException">
    /// The calling thread has too little stack left to read a definition back
    /// (<see cref="SqlPersistedExpression.OutOfStack"/>); inside a statement the session reports it
    /// as <c>COHSQLE004</c>.
    /// </exception>
    internal SqlBoundTable Get(SqlCatalogTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return _bound.TryGetValue(table, out var bound) ? bound : Store(table, Bind(table));
    }

    /// <summary>
    /// Registers the version a catalog mutation published, reusing the bindings of the version
    /// it was derived from when the published one carries the very same column instances, the same
    /// dropped column ordinals and a subset of the same constraint instances — a column appended by the catalog's own copy
    /// of a definition this cache already bound, or a dropped constraint. Anything else binds.
    /// </summary>
    /// <param name="published">The table version the catalog published.</param>
    /// <param name="source">The bound version it was derived from.</param>
    /// <returns>The bound published version.</returns>
    /// <exception cref="DatabaseException">A persisted definition of the table does not load.</exception>
    internal SqlBoundTable Adopt(SqlCatalogTable published, SqlCatalogTable source)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(source);
        if (_bound.TryGetValue(published, out var existing))
        {
            return existing;
        }

        if (!_bound.TryGetValue(source, out var bound) || !SameColumns(published, source) ||
            !published.Constraints.All(constraint => source.Constraints.Any(candidate => ReferenceEquals(candidate, constraint))))
        {
            return Get(published);
        }

        var checks = new List<SqlBoundCheck>();
        foreach (var constraint in published.Constraints)
        {
            if (constraint.Kind == SqlCatalogConstraintKind.Check)
            {
                checks.Add(bound.GetCheck(constraint));
            }
        }

        return Store(published, new SqlBoundTable(published, checks.ToArray(), bound.DefaultValues));
    }

    /// <summary>Whether a table version is already bound, without binding it.</summary>
    /// <param name="table">The table version.</param>
    /// <returns><see langword="true"/> when the version is bound.</returns>
    internal bool IsBound(SqlCatalogTable table) => _bound.TryGetValue(table, out _);

    private SqlBoundTable Store(SqlCatalogTable table, SqlBoundTable bound)
    {
        lock (_sync)
        {
            if (_bound.TryGetValue(table, out var existing))
            {
                return existing;
            }

            _bound.Add(table, bound);
            return bound;
        }
    }

    private static bool SameColumns(SqlCatalogTable left, SqlCatalogTable right)
    {
        if (left.Columns.Count != right.Columns.Count || !left.DroppedColumnOrdinals.SequenceEqual(right.DroppedColumnOrdinals))
        {
            return false;
        }

        for (int ordinal = 0; ordinal < left.Columns.Count; ordinal++)
        {
            if (!ReferenceEquals(left.Columns[ordinal], right.Columns[ordinal]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Binds every table the catalog holds; called when the database opens.</summary>
    /// <exception cref="DatabaseException">
    /// A persisted definition does not load, or the opening thread has too little stack left to
    /// read one back.
    /// </exception>
    internal void BindCatalog()
    {
        foreach (var table in _catalog.Tables)
        {
            try
            {
                Get(table);
            }
            catch (InsufficientExecutionStackException exception)
            {
                // Only the open knows that a larger stack is the remedy (#1151); the message
                // already names the definition and says the catalog is not damaged.
                throw new DatabaseException($"{exception.Message} Open the database on a thread with a larger stack.", exception);
            }
        }
    }

    private SqlBoundTable Bind(SqlCatalogTable table)
    {
        Interlocked.Increment(ref _bindCount);
        string tableName = $"{table.Schema}.{table.Name}";

        var checks = new List<SqlBoundCheck>();
        foreach (var constraint in table.Constraints)
        {
            if (constraint.Kind != SqlCatalogConstraintKind.Check)
            {
                continue;
            }

            string subject = $"CHECK constraint '{constraint.Name}' on table '{tableName}'";
            var predicate = SqlPersistedExpression.Load(constraint.CheckExpression!, subject);
            int[] ordinals;
            SqlBoundExpression bound;
            try
            {
                // Binding, not the DDL's acceptance rules: see SqlPersistedExpression.Bind. The
                // bound tree is what every write to this version evaluates, so a write resolves no
                // column, function or collation of the predicate again.
                bound = SqlPlanExecutor.BindPersistedCheck(predicate, table, _catalog.DefaultCollation);
                ordinals = ColumnOrdinals(table, predicate);
            }
            catch (SqlEvaluationException exception) when (exception.Code == SqlEvaluationException.FunctionSignatureMismatchCode)
            {
                // Not damage: DDL before #1189 did not match calls against their signatures, so a
                // format-4 catalog can hold such a predicate, which never had a value. Format 4 is
                // unreleased and carries no such definition forward (#1152); the open fails, coded.
                throw new DatabaseException(
                    $"{subject} cannot be loaded: its persisted definition '{constraint.CheckExpression}' calls a function with " +
                    $"arguments the function does not accept ({exception.Message}). {SqlPersistedExpression.UncheckedCallHint}",
                    exception);
            }
            catch (DatabaseException exception)
            {
                throw new DatabaseException(
                    $"{subject} cannot be loaded: its persisted definition '{constraint.CheckExpression}' does not bind to the table " +
                    $"({exception.Message}). {SqlPersistedExpression.DamagedCatalogHint}",
                    exception);
            }
            catch (InsufficientExecutionStackException exception)
            {
                // The walkers check the stack (#1151); a thread too small for the definition is
                // not a damaged catalog. The signal stays an exhausted stack, now naming the
                // definition, so whichever caller asked, the open or a statement, handles it.
                throw SqlPersistedExpression.OutOfStack(subject, exception);
            }

            checks.Add(new SqlBoundCheck(constraint, predicate, ordinals, bound));
        }

        var defaults = new string?[table.Columns.Count];
        for (int ordinal = 0; ordinal < defaults.Length; ordinal++)
        {
            var column = table.Columns[ordinal];
            if (column.DefaultLiteral is not null)
            {
                defaults[ordinal] = SqlPersistedExpression.LoadDefaultValue(column.DefaultLiteral,
                    $"DEFAULT of column '{column.Name}' on table '{tableName}'");
            }
        }

        return new SqlBoundTable(table, checks.ToArray(), defaults);
    }

    private static int[] ColumnOrdinals(SqlCatalogTable table, SqlExpression predicate)
    {
        var ordinals = new List<int>();
        Collect(predicate);
        return ordinals.ToArray();

        void Collect(SqlExpression expression)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (expression is SqlColumnReferenceExpression column)
            {
                for (int ordinal = 0; ordinal < table.Columns.Count; ordinal++)
                {
                    if (string.Equals(table.Columns[ordinal].Name, column.ColumnName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!ordinals.Contains(ordinal))
                        {
                            ordinals.Add(ordinal);
                        }

                        break;
                    }
                }
            }

            foreach (var child in SqlPlanner.Children(expression))
            {
                Collect(child);
            }
        }
    }
}
