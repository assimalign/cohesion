using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The database's bound table versions: successful bindings of a version's persisted CHECKs and
/// DEFAULTs are shared by validated writes.
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
    private readonly SqlFunctionEnvironment _native;
    private readonly object _sync = new();
    private CacheState _state;
    private SqlBindingEntry<CacheState>? _pending;
    private long _bindCount;
    private int _validationWalkCount;

    // The required open-time binding pass is separate from repeat Build validation.
    internal int ValidationWalkCount => Volatile.Read(ref _validationWalkCount);

    /// <summary>Initializes an empty cache over a catalog.</summary>
    /// <param name="catalog">The catalog whose table versions are bound.</param>
    /// <param name="functions">
    /// The engine's function catalog and the database, which persisted definitions bind against and
    /// every statement of the database plans with; the standard library alone when null.
    /// </param>
    /// <param name="cancellationToken">The opening operation's token for definition binding.</param>
    internal SqlBoundTableCache(SqlCatalog catalog, SqlFunctionEnvironment? functions = null, CancellationToken cancellationToken = default)
    {
        _catalog = catalog;
        _native = functions ?? SqlFunctionEnvironment.Standard;
        _state = Load(cancellationToken);
        PrepareDefinitions(_state.Functions, cancellationToken);
    }

    /// <summary>
    /// Gets the engine's function catalog and the database: what a statement of the database
    /// resolves its calls against, and what the bound CHECK predicates were bound against.
    /// </summary>
    internal SqlFunctionEnvironment NativeFunctions => _native;

    internal SqlFunctionEnvironment Functions => Volatile.Read(ref _state).Functions;

    // Metadata loading never executes application code. Body compilation happens per entry,
    // after publication, so a statement needing no such body never waits for its compiler.
    internal void EnsureDefinitions(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _pending) is { } pending)
        {
            var state = pending.Get(cancellationToken);
            lock (_sync)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    Volatile.Write(ref _state, state);
                    Volatile.Write(ref _pending, null);
                }
            }
        }
    }

    internal void RefreshDefinitions(CancellationToken cancellationToken)
    {
        lock (_sync) { Volatile.Write(ref _pending, new SqlBindingEntry<CacheState>(Load)); }
        EnsureDefinitions(cancellationToken);
        PrepareDefinitions(Functions, cancellationToken);
    }

    private CacheState Load(CancellationToken cancellationToken)
        => new(this, SqlDefinitionBinder.Load(_catalog, _native, cancellationToken));

    private void PrepareDefinitions(SqlFunctionEnvironment functions, CancellationToken cancellationToken)
    {
        foreach (var function in functions.Catalog.OfType<SqlExpressionFunction>())
        {
            function.PrepareBody(_catalog.DefaultCollation, cancellationToken);
        }
        foreach (var type in functions.Types)
        {
            if (type.Check is SqlExpressionFunction check) { check.PrepareBody(_catalog.DefaultCollation, cancellationToken); }
        }
    }

    private CacheState GetState(CancellationToken cancellationToken)
    {
        EnsureDefinitions(cancellationToken);
        return Volatile.Read(ref _state);
    }

    /// <summary>
    /// Gets how many table versions this cache has bound. Binding is the only place the engine
    /// parses persisted catalog text, which lets tests prove the write path parses nothing.
    /// </summary>
    internal long BindCount => Interlocked.Read(ref _bindCount);

    /// <summary>Gets the bound form of a table version, binding it on first use.</summary>
    /// <param name="table">The table version.</param>
    /// <param name="cancellationToken">The binding operation's token, never retained by the cached tree.</param>
    /// <returns>The bound table version.</returns>
    /// <exception cref="DatabaseException">A persisted definition of the table does not load.</exception>
    /// <exception cref="InsufficientExecutionStackException">
    /// The calling thread has too little stack left to read a definition back
    /// (<see cref="SqlPersistedExpression.OutOfStack"/>); inside a statement the session reports it
    /// as <c>COHSQLE004</c>.
    /// </exception>
    internal SqlBoundTable Get(SqlCatalogTable table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        return GetState(cancellationToken).Get(table, cancellationToken);
    }

    // DDL has already evaluated immutable defaults before its first mutation. Retain
    // those exact bindings instead of folding them again while publishing this version.
    internal SqlBoundTable GetDeclared(SqlCatalogTable table, IReadOnlyList<SqlBoundExpression?> defaults, CancellationToken cancellationToken)
    {
        var state = GetState(cancellationToken);
        var entry = new SqlBindingEntry<SqlBoundTable>(Bind(table, state.Functions, cancellationToken, defaults));
        return state.Bound.GetValue(table, _ => entry).Get(cancellationToken);
    }

    /// <summary>
    /// Registers the version a catalog mutation published, reusing the bindings of the version
    /// it was derived from when the published one carries the very same column instances, the same
    /// dropped column ordinals and a subset of the same constraint instances — a column appended by the catalog's own copy
    /// of a definition this cache already bound, or a dropped constraint. Anything else binds.
    /// </summary>
    /// <param name="published">The table version the catalog published.</param>
    /// <param name="source">The bound version it was derived from.</param>
    /// <param name="cancellationToken">The operation's token if the published version needs binding.</param>
    /// <returns>The bound published version.</returns>
    /// <exception cref="DatabaseException">A persisted definition of the table does not load.</exception>
    internal SqlBoundTable Adopt(SqlCatalogTable published, SqlCatalogTable source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(source);
        var state = GetState(cancellationToken);
        if (state.Bound.TryGetValue(published, out var existing))
        {
            return existing.Get(cancellationToken);
        }

        if (!state.Bound.TryGetValue(source, out var entry) || entry.Value is not { } bound || !SameColumns(published, source) ||
            !published.Constraints.All(constraint => source.Constraints.Any(candidate => ReferenceEquals(candidate, constraint))))
        {
            return state.Get(published, cancellationToken);
        }

        var checks = new List<SqlBoundCheck>();
        foreach (var constraint in published.Constraints)
        {
            if (constraint.Kind == SqlCatalogConstraintKind.Check)
            {
                checks.Add(bound.GetCheck(constraint));
            }
        }

        var adopted = new SqlBindingEntry<SqlBoundTable>(new SqlBoundTable(published, checks.ToArray(), bound.DefaultValues, _catalog.DefaultCollation, bound.Domains, bound.MissingValues));
        return state.Bound.GetValue(published, _ => adopted).Get(cancellationToken);
    }

    /// <summary>Whether a table version is already bound, without binding it.</summary>
    /// <param name="table">The table version.</param>
    /// <returns><see langword="true"/> when the version is bound.</returns>
    internal bool IsBound(SqlCatalogTable table)
        => Volatile.Read(ref _state).Bound.TryGetValue(table, out var entry) && entry.Value is not null;

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
    /// <param name="cancellationToken">The opening operation's token.</param>
    /// <exception cref="DatabaseException">
    /// A persisted definition does not load, or the opening thread has too little stack left to
    /// read one back.
    /// </exception>
    internal void BindCatalog(CancellationToken cancellationToken = default)
    {
        try
        {
            GetValidation(cancellationToken, countWalk: false);
            new SqlPlanner(_catalog, null, Functions, cancellationToken).BindViews();
        }
        catch (InsufficientExecutionStackException exception)
        {
            // Only the open knows that a larger stack is the remedy (#1151).
            string message = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogStackInsufficient, exception.Message);
            throw new DatabaseException(message, [new Diagnostic
            {
                Code = SqlEvaluationException.StatementTooComplexCode,
                Message = message,
                Severity = DiagnosticSeverity.Error,
            }], exception);
        }
    }

    private Validation ValidateCatalog(CacheState state, CancellationToken cancellationToken)
    {
        SqlUnresolvedDefinition? definition = null;
        SqlUnresolvedDomain? unresolvedDomain = null;
        foreach (var function in state.Functions.Catalog.OfType<SqlExpressionFunction>())
        {
            if (function.Unresolved is { } reason) { definition ??= new SqlUnresolvedDefinition("SQL function '" + function.Name + "'", function.Name, reason); }
        }
        foreach (var type in state.Functions.Types)
        {
            if (type.Check is SqlExpressionFunction { Unresolved: { } reason }) { definition ??= new SqlUnresolvedDefinition("SQL domain '" + type.Name + "'", type.Name, reason); }
        }
        foreach (var table in _catalog.Tables)
        {
            var bound = state.Get(table, cancellationToken);
            foreach (var column in table.Columns)
            {
                if (column.DefaultLiteral is not { } text) { continue; }
                var expression = SqlPersistedExpression.Load(text, "DEFAULT");
                var scope = new SqlExpressionEvaluator([], null, defaultCollation: _catalog.DefaultCollation, functions: state.Functions);
                SqlDefinitionBinder.PinCalls(expression, scope, column.DefaultFunctionCalls);
                if (SqlDefinitionBinder.HasUnavailableCall(expression, scope))
                {
                    definition ??= new SqlUnresolvedDefinition("DEFAULT of '" + table.Name + "." + column.Name + "'", text, "which this engine does not register");
                }
            }
            foreach (var check in bound.Checks)
            {
                definition ??= check.Unresolved;
            }
            foreach (var domain in bound.Domains)
            {
                unresolvedDomain ??= domain.Unresolved;
            }
        }

        return new Validation(definition, unresolvedDomain);
    }

    private Validation GetValidation(CancellationToken cancellationToken, bool countWalk = true)
    {
        var state = GetState(cancellationToken);
        ulong generation = _catalog.DdlGeneration;
        SqlBindingEntry<Validation> entry;
        lock (_sync)
        {
            if (state.Validation is null || state.ValidatedGeneration != generation)
            {
                state.ValidatedGeneration = generation;
                state.Validation = new SqlBindingEntry<Validation>(token =>
                {
                    if (countWalk) { Interlocked.Increment(ref _validationWalkCount); }
                    PrepareDefinitions(state.Functions, token);
                    return ValidateCatalog(state, token);
                });
            }
            entry = state.Validation;
        }
        return entry.Get(cancellationToken);
    }

    private SqlBoundTable Bind(SqlCatalogTable table, SqlFunctionEnvironment functions, CancellationToken cancellationToken,
        IReadOnlyList<SqlBoundExpression?>? declaredDefaults = null)
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
                checks.Add(BindCheck(table, constraint, predicate, subject, functions, cancellationToken));
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
        var missing = new SqlBoundExpression?[table.Columns.Count];
        List<SqlBoundDomain>? domains = null;
        for (int ordinal = 0; ordinal < defaults.Length; ordinal++)
        {
            var column = table.Columns[ordinal];
            if (column.DefaultLiteral is not null)
            {
                // Converted to the column's type once, here: no decoded row and no INSERT converts it again.
                defaults[ordinal] = declaredDefaults?[ordinal] ?? SqlPlanExecutor.BindDefaultExpression(column,
                    $"DEFAULT of column '{column.Name}' on table '{tableName}'", functions, _catalog.DefaultCollation, cancellationToken);
            }

            if (column.MissingValueLiteral is { } text)
            {
                var frozen = new SqlCatalogColumn(column.Name, column.Type, true, text, column.Collation);
                if (!SqlPersistedExpression.IsDefaultLiteral(SqlPersistedExpression.Load(text, "Missing value")))
                {
                    throw new SqlExecutionException(SqlExecutionException.DamagedDefinitionCode, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.MissingValueNotLiteral, column.Name));
                }
                missing[ordinal] = string.Equals(text, column.DefaultLiteral, StringComparison.Ordinal) ? defaults[ordinal] : SqlPlanExecutor.BindDefaultExpression(frozen,
                    $"Missing value of column '{column.Name}' on table '{tableName}'", functions, _catalog.DefaultCollation, cancellationToken);
            }
            if (column.DomainName is not null)
            {
                (domains ??= []).Add(BindDomain(table, ordinal, tableName, functions, cancellationToken));
            }
        }

        return new SqlBoundTable(table, checks.ToArray(), defaults, _catalog.DefaultCollation, domains?.ToArray(), missing);
    }

    /// <summary>
    /// Binds a stored column's domain against the engine's type catalog (phase E3): its check as a call
    /// over the column's value, or, when the engine registers no domain of the name, or one over another
    /// storage type or another length, precision or scale than the column stores, an unresolved
    /// binding that fails each write to the column with <c>COHSQLE010</c> while the table opens and its
    /// reads proceed, as owner decision 65 has it for a function. A column keeps the bound it was
    /// created with, so a domain whose base type changed would otherwise bound the column's writes and
    /// a <c>CAST</c> to the domain differently.
    /// </summary>
    private SqlBoundDomain BindDomain(SqlCatalogTable table, int ordinal, string tableName, SqlFunctionEnvironment functions, CancellationToken cancellationToken)
    {
        var column = table.Columns[ordinal];
        string name = column.DomainName!;
        string? reason = !functions.Types.TryGetDomain(name, out var domain)
            ? "which this engine does not register"
            : domain.Storage.Type != column.Type.Type
                || domain.Storage.MaxLength != column.Type.MaxLength
                || domain.Storage.Precision != column.Type.Precision
                || domain.Storage.Scale != column.Type.Scale
                ? $"which this engine registers over {domain.BaseType!.Name}, but the column stores {SqlType.ForStorage(column.Type).Name}"
                : null;
        if (reason is not null)
        {
            return new SqlBoundDomain(ordinal, name, null, new SqlUnresolvedDomain(tableName, column.Name, name, reason));
        }

        if (domain.Check is not { } check)
        {
            return new SqlBoundDomain(ordinal, domain.Name, null, null);
        }

        var call = new SqlBoundCall(check, [new SqlBoundColumn(ordinal)], SqlFunctionResolver.CoercionTargets(check, 1),
            new SqlBoundCollation(column.Collation ?? _catalog.DefaultCollation), functions.Database, cancellationToken);
        return new SqlBoundDomain(ordinal, domain.Name, call, null);
    }

    /// <summary>
    /// Finds the first stored column of the database, in catalog order, whose domain the engine does
    /// not resolve: what an engine build verifies for a database it declares (provisioning step 6,
    /// phase E3), binding every table version it has not bound yet.
    /// </summary>
    /// <returns>The first unresolved domain, or null when every column's domain resolves.</returns>
    /// <exception cref="DatabaseException">A persisted definition of a table does not load.</exception>
    internal SqlUnresolvedDomain? FindUnresolvedDomain(CancellationToken cancellationToken = default)
        => GetValidation(cancellationToken).Domain;

    /// <summary>
    /// Binds one persisted CHECK: its tree when every call resolves, or an unresolved binding that
    /// fails each write with <c>COHSQLE009</c> (owner decision 65) when a call names a function the
    /// engine's catalog does not resolve, or an application function whose registered result no
    /// longer fits where the predicate uses it (a BOOLEAN predicate, a comparison, an arithmetic
    /// operand). Anything else that does not bind is a damaged catalog.
    /// </summary>
    /// <exception cref="DatabaseException">The predicate does not bind and no call explains it.</exception>
    /// <exception cref="InsufficientExecutionStackException">The thread has too little stack left to walk the predicate.</exception>
    private SqlBoundCheck BindCheck(SqlCatalogTable table, SqlCatalogConstraint constraint, SqlExpression predicate, string subject, SqlFunctionEnvironment functions, CancellationToken cancellationToken)
    {
        DatabaseException failure;
        try
        {
            // Binding, not the DDL's acceptance rules: see SqlPersistedExpression.Bind. The bound
            // tree is what every write to this version evaluates, so a write resolves no column,
            // function or collation of the predicate again.
            var bound = SqlPlanExecutor.BindPersistedCheck(predicate, table, _catalog.DefaultCollation, functions, constraint.CheckFunctionCalls, cancellationToken);
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
        var unresolved = FindUnresolvedCall(table, predicate, subject, constraint.CheckFunctionCalls, functions);
        if (unresolved is null && failure is SqlFunctionResultMismatchException mismatch)
        {
            // An application function the engine registers, with a result that no longer fits where
            // the stored predicate uses it: its registration changed after the predicate was stored.
            unresolved = new SqlUnresolvedDefinition(subject, Signature(table, mismatch.Call, functions),
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
                SqlExecutionException.DiagnosticsOf(failure, SqlExecutionException.DamagedDefinitionCode, discardLocations: true), failure);
        }

        throw new DatabaseException(
            $"{subject} cannot be loaded: its persisted definition '{constraint.CheckExpression}' does not bind to the table " +
            $"({failure.Message}). {SqlPersistedExpression.DamagedCatalogHint}",
            SqlExecutionException.DiagnosticsOf(failure, SqlExecutionException.DamagedDefinitionCode, discardLocations: true), failure);
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
    private SqlUnresolvedDefinition? FindUnresolvedCall(SqlCatalogTable table, SqlExpression predicate, string subject, IReadOnlyList<string> identities, SqlFunctionEnvironment functions)
    {
        var scope = new SqlExpressionEvaluator(table.Columns, null, defaultCollation: _catalog.DefaultCollation, functions: functions);
        SqlDefinitionBinder.PinCalls(predicate, scope, identities);
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

            if (expression is not SqlFunctionCallExpression call || SqlStandardLibrary.IsEvaluatorForm(call.FunctionName) ||
                Unresolved(call) is not { } reason)
            {
                return null;
            }

            return new SqlUnresolvedDefinition(subject, Signature(scope, call), reason);
        }

        string? Unresolved(SqlFunctionCallExpression call)
        {
            if (!functions.Catalog.TryGetOverloads(call.FunctionName, out var overloads))
            {
                return "which this engine does not register";
            }
            if (overloads[0].Kind != SqlFunctionKind.Scalar)
            {
                return "which this engine registers as an aggregate";
            }

            try
            {
                var resolved = scope.ResolveFunction(call);
                if (resolved is null)
                {
                    // Resolve only to explain the missing identity; never use the replacement.
                    var diagnostic = new SqlExpressionEvaluator(table.Columns, null, defaultCollation: _catalog.DefaultCollation, functions: functions);
                    _ = diagnostic.ResolveFunction(call);
                    return $"whose recorded identity '{SqlDefinitionBinder.DescribeIdentity(scope.RecordedIdentity(call)!)}' this engine does not register";
                }
                return resolved.Volatility != SqlFunctionVolatility.Immutable
                    ? "which this engine no longer registers as IMMUTABLE"
                    : resolved is SqlExpressionFunction { Unresolved: { } reason } ? reason : null;
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
    private string Signature(SqlCatalogTable table, SqlFunctionCallExpression call, SqlFunctionEnvironment functions)
        => Signature(new SqlExpressionEvaluator(table.Columns, null, defaultCollation: _catalog.DefaultCollation, functions: functions), call);

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
    internal SqlUnresolvedDefinition? FindUnresolved(CancellationToken cancellationToken = default)
        => GetValidation(cancellationToken).Definition;

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

    private sealed record Validation(SqlUnresolvedDefinition? Definition, SqlUnresolvedDomain? Domain);

    // A refresh replaces the whole state. A slow binding retains its original environment and
    // table map and cannot publish an old result into the replacement generation.
    private sealed class CacheState
    {
        private readonly SqlBoundTableCache _owner;
        internal readonly ConditionalWeakTable<SqlCatalogTable, SqlBindingEntry<SqlBoundTable>> Bound = new();
        internal readonly SqlFunctionEnvironment Functions;
        internal ulong ValidatedGeneration;
        internal SqlBindingEntry<Validation>? Validation;

        internal CacheState(SqlBoundTableCache owner, SqlFunctionEnvironment functions)
        {
            _owner = owner;
            Functions = functions;
        }

        internal SqlBoundTable Get(SqlCatalogTable table, CancellationToken cancellationToken)
        {
            if (!Bound.TryGetValue(table, out var entry)) { entry = Bound.GetValue(table, CreateEntry); }
            return entry.Get(cancellationToken);
        }

        private SqlBindingEntry<SqlBoundTable> CreateEntry(SqlCatalogTable table)
            => new(token => _owner.Bind(table, Functions, token));
    }
}
