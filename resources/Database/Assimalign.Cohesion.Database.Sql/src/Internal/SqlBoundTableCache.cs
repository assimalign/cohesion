using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
    /// <param name="functions">
    /// The engine's function catalog and the database, which persisted definitions bind against and
    /// every statement of the database plans with; the standard library alone when null.
    /// </param>
    internal SqlBoundTableCache(SqlCatalog catalog, SqlFunctionEnvironment? functions = null)
    {
        _catalog = catalog;
        Functions = functions ?? SqlFunctionEnvironment.Standard;
    }

    /// <summary>
    /// Gets the engine's function catalog and the database: what a statement of the database
    /// resolves its calls against, and what the bound CHECK predicates were bound against.
    /// </summary>
    internal SqlFunctionEnvironment Functions { get; }

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
            try
            {
                checks.Add(BindCheck(table, constraint, predicate, subject));
            }
            catch (InsufficientExecutionStackException exception)
            {
                // The walkers check the stack (#1151); a thread too small for the definition is
                // not a damaged catalog. The signal stays an exhausted stack, now naming the
                // definition, so whichever caller asked, the open or a statement, handles it.
                throw SqlPersistedExpression.OutOfStack(subject, exception);
            }
        }

        var defaults = new SqlBoundExpression?[table.Columns.Count];
        for (int ordinal = 0; ordinal < defaults.Length; ordinal++)
        {
            var column = table.Columns[ordinal];
            if (column.DefaultLiteral is not null)
            {
                // Converted to the column's type once, here: no decoded row and no INSERT converts it again.
                defaults[ordinal] = SqlPlanExecutor.BindDefault(column, SqlPersistedExpression.LoadDefaultValue(column.DefaultLiteral,
                    $"DEFAULT of column '{column.Name}' on table '{tableName}'"));
            }
        }

        return new SqlBoundTable(table, checks.ToArray(), defaults);
    }

    /// <summary>
    /// Binds one persisted CHECK: its tree when every call resolves, or an unresolved binding that
    /// fails each write with <c>COHSQLE009</c> (owner decision 65) when a call names a function the
    /// engine's catalog does not resolve, or an application function whose registered result no
    /// longer fits where the predicate uses it (a BOOLEAN predicate, a comparison, an arithmetic
    /// operand). Anything else that does not bind is a damaged catalog.
    /// </summary>
    /// <exception cref="DatabaseException">The predicate does not bind and no call explains it.</exception>
    /// <exception cref="InsufficientExecutionStackException">The thread has too little stack left to walk the predicate.</exception>
    private SqlBoundCheck BindCheck(SqlCatalogTable table, SqlCatalogConstraint constraint, SqlExpression predicate, string subject)
    {
        DatabaseException failure;
        try
        {
            // Binding, not the DDL's acceptance rules: see SqlPersistedExpression.Bind. The bound
            // tree is what every write to this version evaluates, so a write resolves no column,
            // function or collation of the predicate again.
            var bound = SqlPlanExecutor.BindPersistedCheck(predicate, table, _catalog.DefaultCollation, Functions);
            return new SqlBoundCheck(constraint, predicate, ColumnOrdinals(table, predicate), bound);
        }
        catch (DatabaseException exception)
        {
            failure = exception;
        }

        // A call the engine's catalog does not resolve is an application change, not damage: the
        // predicate binds as unresolved, the table opens and its reads proceed, and each write that
        // would evaluate the predicate fails with COHSQLE009. Looked for only once binding failed, so
        // a predicate that binds costs nothing more.
        var unresolved = FindUnresolvedCall(table, predicate, subject);
        if (unresolved is null && failure is SqlFunctionResultMismatchException mismatch)
        {
            // An application function the engine registers, with a result that no longer fits where
            // the stored predicate uses it: its registration changed after the predicate was stored.
            unresolved = new SqlUnresolvedDefinition(subject, Signature(table, mismatch.Call),
                $"which this engine registers returning {mismatch.Returned}, where the CHECK needs {mismatch.Needed}");
        }

        if (unresolved is not null)
        {
            return new SqlBoundCheck(constraint, predicate, ColumnOrdinals(table, predicate),
                new SqlBoundFailure(() => throw SqlEvaluationException.UnregisteredFunction(unresolved)), unresolved);
        }

        if (failure is SqlEvaluationException { Code: SqlEvaluationException.FunctionSignatureMismatchCode })
        {
            // Not damage: DDL before #1189 did not match calls against their signatures, so a
            // format-4 catalog can hold such a predicate, which never had a value. Format 4 is
            // unreleased and carries no such definition forward (#1152); the open fails, coded.
            throw new DatabaseException(
                $"{subject} cannot be loaded: its persisted definition '{constraint.CheckExpression}' calls a function with " +
                $"arguments the function does not accept ({failure.Message}). {SqlPersistedExpression.UncheckedCallHint}",
                failure);
        }

        throw new DatabaseException(
            $"{subject} cannot be loaded: its persisted definition '{constraint.CheckExpression}' does not bind to the table " +
            $"({failure.Message}). {SqlPersistedExpression.DamagedCatalogHint}",
            failure);
    }

    /// <summary>
    /// Finds the first call of a persisted predicate, its arguments before itself, that names a
    /// function the engine's catalog does not resolve: a name it does not register, a name it
    /// registers as an aggregate, or a registered name none of whose overloads, or more than one,
    /// accepts the call. A call a built-in takes by its shape (<c>UPPER(x)</c>) always resolves to the
    /// built-in, so one that does not bind is left to the binder's own error (#1189); a call of a
    /// standard-library name no built-in takes (<c>upper(x, 2)</c>) can only have called an
    /// application's overload, so it is unresolved like any application function's.
    /// </summary>
    /// <returns>The unresolved call, or null when every call resolves.</returns>
    /// <exception cref="InsufficientExecutionStackException">The thread has too little stack left to walk the predicate.</exception>
    private SqlUnresolvedDefinition? FindUnresolvedCall(SqlCatalogTable table, SqlExpression predicate, string subject)
    {
        var scope = new SqlExpressionEvaluator(table.Columns, null, defaultCollation: _catalog.DefaultCollation, functions: Functions);
        return Find(predicate);

        SqlUnresolvedDefinition? Find(SqlExpression expression)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            foreach (var child in SqlPlanner.Children(expression))
            {
                if (Find(child) is { } found)
                {
                    return found;
                }
            }

            if (expression is not SqlFunctionCallExpression call || SqlStandardLibrary.IsCoalesce(call.FunctionName) ||
                Unresolved(call) is not { } reason)
            {
                return null;
            }

            return new SqlUnresolvedDefinition(subject, Signature(scope, call), reason);
        }

        string? Unresolved(SqlFunctionCallExpression call)
        {
            if (!Functions.Catalog.TryGetOverloads(call.FunctionName, out var overloads))
            {
                return "which this engine does not register";
            }
            if (SqlStandardLibrary.AcceptsShape(call.FunctionName, call.Arguments))
            {
                return null;
            }
            if (overloads[0].Kind != SqlFunctionKind.Scalar)
            {
                return "which this engine registers as an aggregate";
            }

            try
            {
                scope.ResolveFunction(call);
                return null;
            }
            catch (SqlEvaluationException exception) when (exception.Code == SqlEvaluationException.AmbiguousFunctionCallCode)
            {
                return $"which more than one overload this engine registers accepts equally well ({exception.Message})";
            }
            catch (SqlEvaluationException exception) when (exception.Code == SqlEvaluationException.FunctionSignatureMismatchCode)
            {
                return $"which no overload this engine registers accepts ({exception.Message})";
            }
        }
    }

    /// <summary>A call as a persisted definition makes it, its arguments typed over the table: <c>slugify(TEXT)</c>.</summary>
    private string Signature(SqlCatalogTable table, SqlFunctionCallExpression call)
        => Signature(new SqlExpressionEvaluator(table.Columns, null, defaultCollation: _catalog.DefaultCollation, functions: Functions), call);

    private static string Signature(SqlExpressionEvaluator scope, SqlFunctionCallExpression call)
    {
        var signature = new StringBuilder(call.FunctionName).Append('(');
        for (int index = 0; index < call.Arguments.Count; index++)
        {
            signature.Append(index == 0 ? string.Empty : ", ").Append(call.Arguments[index] is SqlStarExpression
                ? "*"
                : SqlType.NameOf(scope.StaticTypeOf(call.Arguments[index])));
        }

        return signature.Append(')').ToString();
    }

    /// <summary>
    /// Finds the first persisted definition of the database, in catalog order, that calls a function
    /// the engine does not resolve: what an engine build verifies for a database it declares
    /// (provisioning step 6, owner decision 65), binding every table version it has not bound yet.
    /// </summary>
    /// <returns>The first unresolved definition, or null when every definition binds.</returns>
    /// <exception cref="DatabaseException">A persisted definition of a table does not load.</exception>
    internal SqlUnresolvedDefinition? FindUnresolved()
    {
        foreach (var table in _catalog.Tables)
        {
            foreach (var check in Get(table).Checks)
            {
                if (check.Unresolved is { } unresolved)
                {
                    return unresolved;
                }
            }
        }

        return null;
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
