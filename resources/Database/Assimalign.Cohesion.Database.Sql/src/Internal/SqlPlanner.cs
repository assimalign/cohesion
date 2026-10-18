using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The rule-based planner: binds a parsed statement against the catalog, validates
/// it against the executor's supported surface, and produces a <see cref="SqlPlan"/>.
/// Unsupported dialect features fail here with precise messages rather than
/// misexecuting.
/// </summary>
internal sealed partial class SqlPlanner
{
    /// <summary>
    /// The schema used when a table reference has none.
    /// </summary>
    internal const string DefaultSchema = "dbo";

    /// <summary>The dialect's name for a row of an INSERT's table value constructor, in diagnostics.</summary>
    private const string insertValuesClause = "INSERT ... VALUES";

    private readonly SqlCatalog _catalog;

    private readonly SqlStatementMetrics? _statementMetrics;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;
    private readonly CancellationToken _cancellationToken;

    /// <summary>The engine's function catalog and the database, which every call of the statement resolves against.</summary>
    private readonly SqlFunctionEnvironment _functions;

    /// <summary>Backs <see cref="ScopelessEvaluator"/>.</summary>
    private SqlExpressionEvaluator? _scopelessEvaluator;

    /// <summary>Initializes a planner for one statement.</summary>
    /// <param name="catalog">The database's catalog.</param>
    /// <param name="parameters">The statement's parameter values.</param>
    /// <param name="functions">The engine's function catalog and the database; the standard library alone when null.</param>
    /// <param name="cancellationToken">The statement's token, including calls made while binding or evaluating counts and seek bounds.</param>
    /// <param name="statement">The shared execution context whose catalog image is requested by relation binding; null for detached plan inspection.</param>
    internal SqlPlanner(SqlCatalog catalog, IReadOnlyDictionary<string, object?>? parameters, SqlFunctionEnvironment? functions = null,
        CancellationToken cancellationToken = default, SqlStatementContext? statement = null)
    {
        _statementMetrics = statement?.Metrics;
        _catalog = catalog;
        _parameters = parameters;
        _cancellationToken = cancellationToken;
        _functions = statement is { } context ? (functions ?? SqlFunctionEnvironment.Standard).At(context.TransactionStart) : functions ?? SqlFunctionEnvironment.Standard;
    }

    private SqlPlanner(SqlPlanner previous)
        : this(previous._catalog, previous._parameters, previous._functions, previous._cancellationToken)
    {
        _statementMetrics = previous._statementMetrics;
    }

    internal SqlPlan Plan(SqlQueryExpression expression)
    {
        var planner = this;
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            ulong generation = _catalog.DdlGeneration;
            try
            {
                var plan = planner.PlanOnce(expression);
                if (_catalog.DdlGeneration == generation) { return plan; }
            }
            catch (DatabaseException) when (_catalog.DdlGeneration != generation)
            {
                // A concurrent publication may pair an old table with new index key names.
                // Discard both successful and failed mixed-generation plans before execution.
            }
            // Fresh per-plan state includes scopes, CTEs and subquery slots. Stable planning
            // allocates no catalog capture and retains the stored-query allocation budget.
            planner = new SqlPlanner(this);
        }
    }

    private SqlPlan PlanOnce(SqlQueryExpression expression)
    {
        try
        {
            var referenced = FindReferencedCommonTables(expression);
            _commonTableState = referenced is null ? null : new CommonTableState(referenced);
            return PlanCore(expression);
        }
        catch (SqlEvaluationException exception) when (exception.Diagnostics[0].Start is null && expression.Location is not null)
        {
            throw exception.At(expression.Location);
        }
        catch (SqlPlanningException exception) when (exception.Diagnostics[0].Start is null && expression.Location is not null)
        {
            throw exception.At(expression.Location);
        }
    }

    private SqlPlan PlanCore(SqlQueryExpression expression)
        => expression switch
        {
            SqlInsertExpression { CommonTables.Count: > 0 } insert => PlanCommonTables(insert.CommonTables, insert),
            SqlUpdateExpression { CommonTables.Count: > 0 } update => PlanCommonTables(update.CommonTables, update),
            SqlDeleteExpression { CommonTables.Count: > 0 } delete => PlanCommonTables(delete.CommonTables, delete),
            _ => PlanStatementCore(expression),
        };

    private SqlPlan PlanStatementCore(SqlQueryExpression expression)
    {
        SqlSystemViews.EnsureReadOnly(expression);
        ValidateFunctionCalls(expression);
        return expression switch
        {
            SqlCreateFunctionExpression or SqlDropFunctionExpression or SqlCreateDomainExpression or SqlDropDomainExpression
                => new SqlDefinitionPlan(expression),
            SqlSelectExpression select => PlanSelect(select),
            SqlInsertExpression insert => PlanInsert(insert),
            SqlUpdateExpression update => PlanUpdate(update),
            SqlDeleteExpression delete => PlanDelete(delete),
            SqlCreateTableExpression create => PlanCreateTable(create),
            SqlDropTableExpression drop => PlanDropTable(drop),
            SqlAlterTableExpression alter => PlanAlterTable(alter),
            SqlCreateIndexExpression createIndex => PlanCreateIndex(createIndex),
            SqlDropIndexExpression dropIndex => PlanDropIndex(dropIndex),
            _ => throw new SqlPlanningException("COHSQLE013", $"Statement type {expression.CommandType} is not supported by the executor yet.", expression.Location),
        };
    }

    private SqlPlan PlanSelect(SqlSelectExpression select)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return select.CommonTables.Count == 0 ? PlanSubqueries(select)
            : PlanCommonTables(select.CommonTables, select);
    }

    /// <summary>Binds a SELECT whose subqueries have already been lowered to value slots.</summary>
    private SqlPlan PlanSelectCore(SqlSelectExpression select)
    {
        if (select.Where is not null && ContainsAggregate(select.Where))
        {
            throw new SqlPlanningException("COHSQLE017", "Aggregate functions are not allowed in WHERE; use HAVING to filter groups.", select.Where?.Location);
        }
        if (select.From.Count == 0)
        {
            if (ReferenceEquals(_commonTableState?.BindingBody, select)) { return PlanConstantCommonTable(select); }
            throw new SqlPlanningException("COHSQLE013", "SELECT requires a FROM table.", select.Location);
        }

        var source = ResolveRelation(select.From[0].Table, select);
        var table = source.Table;
        var systemView = source.View;
        IReadOnlyList<SqlRelationBinding>? bindings = null;
        IReadOnlyList<SqlCatalogColumn> columns = source.Columns;
        IReadOnlyList<SqlJoinStep> steps = [];
        IReadOnlyList<int>? outputOrdinals = null;
        if (select.From.Count > 1 || select.From[0].Joins.Count > 0 || source.Derived is not null)
        {
            bindings = BindJoin(select, source, out columns, out steps, out outputOrdinals);
        }
        var evaluator = new SqlExpressionEvaluator(columns, _parameters, bindings, defaultCollation: _catalog.DefaultCollation,
            subquerySlots: _subquerySlots, functions: _functions, cancellationToken: _cancellationToken,
            outerScope: OuterScope, outerDependencies: OuterDependencies, relation: select.From[0].Table);

        if (select.GroupBy.Count > 0 || select.Having is not null || ProjectsOrOrdersByAggregate(select))
        {
            return PlanGroup(select, table, systemView, columns, bindings, evaluator, steps);
        }

        var projections = new List<SqlProjection>(select.Columns.Count);
        foreach (var column in select.Columns)
        {
            if (column.Expression is SqlStarExpression)
            {
                for (int i = 0; i < (outputOrdinals?.Count ?? columns.Count); i++)
                {
                    int ordinal = outputOrdinals is null ? i : outputOrdinals[i];
                    projections.Add(PassThrough(columns[ordinal].Name, ordinal, columns, evaluator, merged: true));
                }
            }
            else if (column.Expression is SqlColumnReferenceExpression reference && !evaluator.IsOuterColumn(reference))
            {
                int ordinal = evaluator.ResolveColumn(reference);
                projections.Add(PassThrough(column.Alias ?? columns[ordinal].Name, ordinal, columns, evaluator,
                    merged: reference.TableAlias is null && reference.SchemaName is null));
            }
            else
            {
                ValidateExpression(column.Expression, evaluator, _subqueryTypes);
                var value = evaluator.Bind(column.Expression, out SqlBoundCollation collation, out var candidate);
                projections.Add(new SqlProjection(
                    column.Alias ?? (column.Expression is SqlColumnReferenceExpression outerColumn
                        ? outerColumn.ColumnName : $"column{projections.Count + 1}"), null, column.Expression,
                    GroupExpressionType(column.Expression, columns, evaluator)) { Value = value, Collation = collation,
                        Metadata = CaptureProjectionMetadata(candidate,
                            UnwrapCollation(column.Expression) is SqlColumnReferenceExpression projectedColumn ? evaluator.ColumnMetadata(projectedColumn) : null),
                        MaxLength = evaluator.MaximumLengthOf(column.Expression),
                        IsNullable = column.Expression is not SqlColumnReferenceExpression nullableColumn || evaluator.ColumnMetadata(nullableColumn).IsNullable });
            }
        }

        SqlBoundExpression? where = null;
        if (select.Where is not null)
        {
            ValidateExpression(select.Where, evaluator, _subqueryTypes);
            where = evaluator.Bind(select.Where);
        }

        var orderByProjections = BindOrderByProjections(select, projections, outputOrdinals?.Count ?? columns.Count);
        if (bindings is not null && select.OrderBy.Count > 0) { RejectDerivedOrdering(select, projections, orderByProjections, evaluator, bindings); }
        foreach (var orderBy in select.OrderBy)
        {
            ValidateExpression(orderBy.Expression, evaluator, _subqueryTypes, orderByProjections);
        }

        // Ordering keys that name an output by alias or position read it from the slots that
        // follow the source row (ProjectAndSortRows); every other key reads the source row.
        var ordering = BindOrdering(select.OrderBy, evaluator.ForOrdering(projections, orderByProjections, columns.Count));

        if (bindings is not null)
        {
            return new SqlJoinPlan(bindings, columns, steps, projections,
                where, ordering, EvaluateCount(select.Limit, LimitClause(select)),
                EvaluateCount(select.Offset, "OFFSET"), select.IsDistinct,
                orderByProjections);
        }

        if (systemView is not null)
        {
            return new SqlSystemViewPlan(systemView, projections, where, ordering,
                EvaluateCount(select.Limit, LimitClause(select)), EvaluateCount(select.Offset, "OFFSET"),
                select.IsDistinct, orderByProjections);
        }

        return new SqlSelectPlan(
            table!,
            projections,
            where,
            ordering,
            EvaluateCount(select.Limit, LimitClause(select)),
            EvaluateCount(select.Offset, "OFFSET"),
            select.IsDistinct,
            SelectAccessPath(table!, select.Where, OuterScope is null ? null : evaluator), orderByProjections);
    }

    /// <summary>Resolves a read source and requests the statement's one catalog image at binding time.</summary>
    private SqlRelationSource ResolveRelation(SqlRelationReference reference, SqlSelectExpression? owner = null)
    {
        // The namespace and relational operators share this binding. Constructing a subquery
        // scope must never re-plan a derived body or number its subquery slots again.
        var key = (owner, reference);
        if (_queryScopes is not null && _queryScopes.Relations.TryGetValue(key, out var bound)) { return bound; }
        var source = ResolveRelationCore(reference, owner);
        if (_queryScopes is not null) { _queryScopes.Relations[key] = source; }
        return source;
    }

    private SqlRelationSource ResolveRelationCore(SqlRelationReference reference, SqlSelectExpression? owner)
    {
        if (reference is SqlDerivedTableReference derived)
        {
            return BindDerivedTable(derived, owner);
        }
        var named = (SqlTableReference)reference;
        if (named.SchemaName is null && _commonTableState is { } state)
        {
            if (state.Relations?.TryGetValue(named.TableName, out var commonTable) == true)
            {
                state.Dependencies!.Add(commonTable);
                return new SqlRelationSource(commonTable);
            }
        }
        if (SqlSystemViews.Find(named) is { } view)
        {
            _statementMetrics?.CaptureCatalogSnapshot(_catalog);
            return new SqlRelationSource(view);
        }
        // Future CTEs are a missing-relation hint, not a catalog shadow (PostgreSQL
        // src/backend/parser/parse_relation.c:1463-1487).
        if (named.SchemaName is null && _commonTableState?.Future?.Contains(named.TableName) == true &&
            !_catalog.TryGetTable(DefaultSchema, named.TableName, out _))
        {
            throw new SqlPlanningException("COHSQLE026", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.CteForwardReference, named.TableName), owner?.Location);
        }
        return new SqlRelationSource(ResolveTable(named));
    }

    /// <summary>A projection that passes a source column through, with the collation <c>DISTINCT</c> compares it under.</summary>
    private SqlProjection PassThrough(string name, int ordinal, IReadOnlyList<SqlCatalogColumn> columns,
        SqlExpressionEvaluator scope, bool merged = false)
    {
        var output = merged ? scope.MergedColumn(ordinal) : null;
        var column = output?.Column ?? columns[ordinal];
        return new(name, output is null ? ordinal : null, null, column.Type.Type)
        {
            Value = output?.Value, Collation = new SqlBoundCollation(scope.ResolveColumnCollation(ordinal)),
            Metadata = CaptureProjectionMetadata(new SqlCollationCandidate(column.Collation, column.Collation is null ? 0 : 2), column),
            MaxLength = column.Type.MaxLength, IsNullable = column.IsNullable,
        };
    }

    /// <summary>Binds <c>ORDER BY</c> keys in their ordering scope, each with the collation it sorts under.</summary>
    /// <param name="orderBy">The keys.</param>
    /// <param name="scope">The ordering scope (<see cref="SqlExpressionEvaluator.ForOrdering"/>).</param>
    /// <returns>The bound keys.</returns>
    private static IReadOnlyList<SqlBoundOrdering> BindOrdering(IReadOnlyList<SqlOrderByColumn> orderBy, SqlExpressionEvaluator scope)
    {
        if (orderBy.Count == 0)
        {
            return [];
        }

        var ordering = new SqlBoundOrdering[orderBy.Count];
        for (int index = 0; index < ordering.Length; index++)
        {
            var key = scope.Bind(orderBy[index].Expression, out SqlBoundCollation collation);
            ordering[index] = new SqlBoundOrdering(key, orderBy[index].IsDescending, collation);
        }

        return ordering;
    }

    // ── Access-path selection (rule-based, by design) ──────────────────

    /// <summary>
    /// Picks the SELECT's access path: an index seek when the WHERE clause has
    /// sargable predicates on an index's leading key columns, the per-object
    /// scan otherwise. Selection is rule-based (the MVP planner's contract —
    /// no cost model): the index with the longest equality prefix wins; ties
    /// prefer a usable range bound, then uniqueness, then name (deterministic).
    /// The full WHERE always remains the residual predicate, so a wrong-looking
    /// choice can cost performance but never correctness.
    /// </summary>
    private SqlAccessPath SelectAccessPath(SqlCatalogTable table, SqlExpression? where, SqlExpressionEvaluator? scope = null)
    {
        if (where is null || _commonTableState?.BindingUnused == true)
        {
            return SqlScanPath.Instance;
        }

        var indexes = _catalog.GetIndexes(table.ObjectId);

        if (indexes.Count == 0)
        {
            return SqlScanPath.Instance;
        }

        // Top-level AND conjuncts → per-column sargable predicates.
        var predicates = new Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>>();
        CollectSargablePredicates(table, where, predicates, scope);

        if (predicates.Count == 0)
        {
            return SqlScanPath.Instance;
        }

        SqlIndexSeekPath? best = null;
        int bestPrefix = -1;
        bool bestHasRange = false;

        foreach (var index in indexes.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase))
        {
            var seek = TryBuildSeek(table, index, predicates, out int prefixLength, out bool hasRange);

            if (seek is null)
            {
                continue;
            }

            bool better =
                prefixLength > bestPrefix
                || (prefixLength == bestPrefix && hasRange && !bestHasRange)
                || (prefixLength == bestPrefix && hasRange == bestHasRange && index.IsUnique && best is { Index.IsUnique: false });

            if (better)
            {
                best = seek;
                bestPrefix = prefixLength;
                bestHasRange = hasRange;
            }
        }

        return (SqlAccessPath?)best ?? SqlScanPath.Instance;
    }

    /// <summary>
    /// Builds a seek over one index from the collected predicates: the longest
    /// equality prefix over the leading key columns, plus range bounds on the
    /// following key column when its type's evaluator order provably matches the
    /// codec's byte order. Returns null when the index contributes nothing.
    /// </summary>
    private SqlIndexSeekPath? TryBuildSeek(
        SqlCatalogTable table,
        SqlCatalogIndex index,
        Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>> predicates,
        out int prefixLength,
        out bool hasRange)
    {
        prefixLength = 0;
        hasRange = false;

        var equalityValues = new List<object?>();

        foreach (string columnName in index.ColumnNames)
        {
            int ordinal = FindColumnOrdinal(table, columnName);

            if (!predicates.TryGetValue(ordinal, out var columnPredicates))
            {
                break;
            }

            object? equality = null;
            bool hasEquality = false;

            foreach (var (op, value) in columnPredicates)
            {
                if (op == SqlBinaryOperator.Equal)
                {
                    equality = value;
                    hasEquality = true;
                    break;
                }
            }

            if (!hasEquality)
            {
                // No equality on this key column: a range bound here ends the seek.
                if (IsRangeSargable(table.Columns[ordinal].Type.Type))
                {
                    SqlSeekBound? lower = null;
                    SqlSeekBound? upper = null;

                    foreach (var (op, value) in columnPredicates)
                    {
                        switch (op)
                        {
                            case SqlBinaryOperator.GreaterThan:
                                lower = Tighter(lower, new SqlSeekBound(value, Inclusive: false), isLower: true, table.Columns[ordinal].Collation ?? _catalog.DefaultCollation);
                                break;
                            case SqlBinaryOperator.GreaterOrEqual:
                                lower = Tighter(lower, new SqlSeekBound(value, Inclusive: true), isLower: true, table.Columns[ordinal].Collation ?? _catalog.DefaultCollation);
                                break;
                            case SqlBinaryOperator.LessThan:
                                upper = Tighter(upper, new SqlSeekBound(value, Inclusive: false), isLower: false, table.Columns[ordinal].Collation ?? _catalog.DefaultCollation);
                                break;
                            case SqlBinaryOperator.LessOrEqual:
                                upper = Tighter(upper, new SqlSeekBound(value, Inclusive: true), isLower: false, table.Columns[ordinal].Collation ?? _catalog.DefaultCollation);
                                break;
                        }
                    }

                    if (lower is not null || upper is not null)
                    {
                        prefixLength = equalityValues.Count;
                        hasRange = true;
                        return new SqlIndexSeekPath(index, equalityValues, lower, upper);
                    }
                }

                break;
            }

            equalityValues.Add(equality);
        }

        if (equalityValues.Count == 0)
        {
            return null;
        }

        prefixLength = equalityValues.Count;
        return new SqlIndexSeekPath(index, equalityValues, Lower: null, Upper: null);
    }

    /// <summary>
    /// Keeps the tighter of two candidate bounds (the residual predicate makes
    /// either choice correct; tighter just reads fewer entries).
    /// </summary>
    private static SqlSeekBound? Tighter(SqlSeekBound? current, SqlSeekBound candidate, bool isLower, Collation collation)
    {
        if (current is null || current.Value.Value is null || candidate.Value is null)
        {
            return candidate;
        }

        int comparison = SqlExpressionEvaluator.Compare(candidate.Value, current.Value.Value, collation);
        bool candidateTighter = isLower ? comparison > 0 : comparison < 0;
        return candidateTighter ? candidate : current;
    }

    /// <summary>
    /// Walks the WHERE clause's top-level AND conjuncts and collects sargable
    /// comparisons: <c>column op comparand</c> (either side) where the comparand
    /// is plan-time evaluable (no column references), non-null, and coercible to
    /// the column's storage type, plus <c>column BETWEEN low AND high</c> as its
    /// two bounds. Everything else is left to the residual predicate.
    /// </summary>
    private void CollectSargablePredicates(
        SqlCatalogTable table,
        SqlExpression expression,
        Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>> predicates, SqlExpressionEvaluator? scope = null)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlLogicalExpression { Operator: SqlLogicalOperator.And } conjunction)
        {
            // Every term of the chain at one level; a parenthesized conjunction among them is a
            // nested chain whose terms are just as mandatory.
            foreach (var term in conjunction.Operands)
            {
                CollectSargablePredicates(table, term, predicates, scope);
            }
            return;
        }

        if (expression is SqlBetweenExpression { IsNegated: false } between &&
            UnwrapCollation(between.Operand) is SqlColumnReferenceExpression)
        {
            TryAddPredicate(table, between.Operand, SqlBinaryOperator.GreaterOrEqual, between.Low, predicates, scope: scope);
            TryAddPredicate(table, between.Operand, SqlBinaryOperator.LessOrEqual, between.High, predicates, scope: scope);
            return;
        }

        if (expression is not SqlBinaryExpression binary)
        {
            return;
        }

        switch (binary.Operator)
        {
            case SqlBinaryOperator.Equal:
            case SqlBinaryOperator.LessThan:
            case SqlBinaryOperator.LessOrEqual:
            case SqlBinaryOperator.GreaterThan:
            case SqlBinaryOperator.GreaterOrEqual:
                break;
            default:
                return;
        }

        if (UnwrapCollation(binary.Left) is SqlColumnReferenceExpression)
        {
            TryAddPredicate(table, binary.Left, binary.Operator, binary.Right, predicates, scope: scope);
        }
        else if (UnwrapCollation(binary.Right) is SqlColumnReferenceExpression)
        {
            TryAddPredicate(table, binary.Right, Flip(binary.Operator), binary.Left, predicates, columnOnLeft: false, scope: scope);
        }
    }

    /// <summary>Removes only COLLATE wrappers when recognizing a direct key reference.</summary>
    private static SqlExpression UnwrapCollation(SqlExpression expression)
    {
        while (expression is SqlCollateExpression collate)
        {
            expression = collate.Operand;
        }
        return expression;
    }

    private static SqlBinaryOperator Flip(SqlBinaryOperator op) => op switch
    {
        SqlBinaryOperator.LessThan => SqlBinaryOperator.GreaterThan,
        SqlBinaryOperator.LessOrEqual => SqlBinaryOperator.GreaterOrEqual,
        SqlBinaryOperator.GreaterThan => SqlBinaryOperator.LessThan,
        SqlBinaryOperator.GreaterOrEqual => SqlBinaryOperator.LessOrEqual,
        _ => op,
    };

    private void TryAddPredicate(
        SqlCatalogTable table,
        SqlExpression columnExpression,
        SqlBinaryOperator op,
        SqlExpression comparand,
        Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>> predicates,
        bool columnOnLeft = true, SqlExpressionEvaluator? scope = null)
    {
        var evaluator = scope ?? new SqlExpressionEvaluator(table.Columns, _parameters, defaultCollation: _catalog.DefaultCollation, functions: _functions, cancellationToken: _cancellationToken);
        // A comparand that calls a volatile function can change between the plan and a row.
        if (evaluator.ReferencesTableColumn(comparand) || ContainsVolatileCall(comparand))
        {
            return;
        }

        var column = (SqlColumnReferenceExpression)UnwrapCollation(columnExpression);
        int ordinal;
        try
        {
            ordinal = evaluator.ResolveColumn(column);
            // An enclosing column can filter this child's rows, but cannot narrow its index.
            if (ordinal < 0) { return; }
            if (table.Columns[ordinal].Type.Type is DatabaseType.String or DatabaseType.Character or DatabaseType.Json)
            {
                var indexedCollation = table.Columns[ordinal].Collation ?? _catalog.DefaultCollation;
                var effectiveCollation = columnOnLeft ? evaluator.ResolveCollation(columnExpression, comparand)
                    : evaluator.ResolveCollation(comparand, columnExpression);
                if (!indexedCollation.IsIndexBacked || effectiveCollation != indexedCollation)
                {
                    return; // Different equivalence/order would omit rows before residual evaluation.
                }
            }
        }
        catch (DatabaseException)
        {
            return; // resolution errors belong to validation, not access-path selection
        }

        object? value;
        try
        {
            value = new SqlExpressionEvaluator(Array.Empty<SqlCatalogColumn>(), _parameters, defaultCollation: _catalog.DefaultCollation, functions: _functions, cancellationToken: _cancellationToken)
                .Evaluate(comparand, Array.Empty<object?>());
            if (table.Columns[ordinal].Type.Type == DatabaseType.Character
                && UnwrapCollation(comparand) is SqlLiteralExpression or SqlParameterExpression)
            {
                value = SqlCharacter.Input(value, DatabaseType.Character);
            }
            object? storageValue = SqlPlanExecutor.CoerceForColumn(value, table.Columns[ordinal]);
            // A rounded bound can exclude qualifying rows before residual evaluation
            // (e.g. an INT key > CAST('1.5' AS DECIMAL) must still include 2).
            if (value is not null && storageValue is not null &&
                SqlExpressionEvaluator.Compare(value, storageValue) != 0)
            {
                return;
            }
            value = storageValue;
        }
        catch (DatabaseException)
        {
            return; // not plan-time evaluable, or not coercible: leave it to the residual
        }

        if (value is null)
        {
            // A null comparand can never satisfy a comparison (SQL three-valued
            // logic); the scan's residual evaluation yields the same empty
            // result without special-casing keys.
            return;
        }

        if (op != SqlBinaryOperator.Equal && !IsRangeSargable(table.Columns[ordinal].Type.Type))
        {
            return;
        }

        // Physical IEEE keys distinguish signed zero; SQL equality combines them.
        // A single encoded equality probe would omit the other zero representation.
        if (value is double doubleValue && doubleValue == 0d || value is float floatValue && floatValue == 0f)
        {
            return;
        }

        if (!predicates.TryGetValue(ordinal, out var list))
        {
            list = new List<(SqlBinaryOperator, object?)>();
            predicates[ordinal] = list;
        }

        list.Add((op, value));
    }

    /// <summary>
    /// Range seeks require evaluator order to match encoded byte order. String
    /// predicates also pass the effective-collation compatibility check above.
    /// Guid, binary and JSON remain equality-only. Floating keys place NaN last,
    /// unlike SQL comparison, so even finite range bounds require a scan.
    /// </summary>
    private static bool IsRangeSargable(DatabaseType type) => type switch
    {
        DatabaseType.Boolean or DatabaseType.String or DatabaseType.Character
            or DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64
            or DatabaseType.Decimal
            or DatabaseType.Date or DatabaseType.Time or DatabaseType.DateTime
            or DatabaseType.DateTimeOffset or DatabaseType.TimeSpan => true,
        _ => false,
    };

    private SqlPlan PlanInsert(SqlInsertExpression insert)
    {
        if (!insert.IsDefaultValues && insert.SelectSource is null && (insert.Values is null || insert.Values.Count == 0))
        {
            throw new SqlPlanningException("COHSQLE013", "INSERT requires a VALUES list, DEFAULT VALUES or SELECT source.", insert.Location);
        }

        var table = ResolveTable(insert.Table);

        IReadOnlyList<int> targetOrdinals;

        if (insert.Columns is null)
        {
            targetOrdinals = Enumerable.Range(0, table.Columns.Count).ToList();
        }
        else
        {
            var ordinals = new List<int>(insert.Columns.Count);
            foreach (string name in insert.Columns)
            {
                int ordinal = FindColumnOrdinal(table, name);
                if (ordinals.Contains(ordinal))
                {
                    throw new SqlPlanningException("COHSQLE019", $"INSERT target column '{name}' is specified more than once.", insert.Location);
                }
                ordinals.Add(ordinal);
            }
            targetOrdinals = ordinals;
        }

        for (int index = 0; index < targetOrdinals.Count && !insert.IsDefaultValues; index++)
        {
            var column = table.Columns[targetOrdinals[index]];
            if (column.Identity is not { IsAlways: true }) { continue; }
            if (insert.SelectSource is not null)
            {
                throw SqlPlanningException.IdentityViolation(column.Name, ErrorMessages.IdentityInsertForbidden);
            }
            foreach (var row in insert.Values!)
            {
                if (index < row.Count && row[index] is not SqlDefaultExpression)
                {
                    throw SqlPlanningException.IdentityViolation(column.Name, ErrorMessages.IdentityInsertForbidden);
                }
            }
        }
        if (insert.SelectSource is not null)
        {
            return PlanInsertSelect(table, targetOrdinals, insert.SelectSource);
        }

        if (insert.IsDefaultValues)
        {
            // Validate any column list above, then omit every column through the shared INSERT path.
            return new SqlInsertPlan(table, [], [Array.Empty<SqlBoundExpression>()]);
        }

        foreach (var row in insert.Values!)
        {
            if (row.Count != targetOrdinals.Count)
            {
                throw new SqlPlanningException("COHSQLE013",
                    $"INSERT row has {row.Count} values but {targetOrdinals.Count} target columns.", insert.Location);
            }
        }

        // A VALUES row is evaluated once, against no row, before anything is written; every
        // row is checked here so a bad expression in a later row fails before the first one
        // is inserted (#1165).
        foreach (var row in insert.Values!)
        {
            foreach (var value in row)
            {
                if (value is not SqlDefaultExpression)
                {
                    ValidateScopelessExpression(value, insertValuesClause, "INSERT ... SELECT to read values from a table");
                }
            }
        }

        // Bound with no columns in scope, like the validation above: no reference can resolve to
        // an ordinal the empty row the executor evaluates them against does not have.
        var rows = new SqlBoundExpression[insert.Values!.Count][];
        for (int index = 0; index < rows.Length; index++)
        {
            var row = insert.Values[index];
            var bound = new SqlBoundExpression[row.Count];
            for (int ordinal = 0; ordinal < bound.Length; ordinal++)
            {
                bound[ordinal] = row[ordinal] is SqlDefaultExpression
                    ? SqlBoundDefaultItem.Instance : ScopelessEvaluator.Bind(row[ordinal]);
            }

            rows[index] = bound;
        }

        return new SqlInsertPlan(table, targetOrdinals, rows);
    }

    /// <summary>
    /// Validates an expression of a clause that has no columns in scope: a row of
    /// <c>INSERT ... VALUES</c> (ISO SQL's table value constructor of an INSERT, whose
    /// expressions may not reference a column), and a <c>LIMIT</c> or <c>OFFSET</c> count. Such
    /// an expression is evaluated against an empty row, so a column reference has nothing to
    /// bind to: before #1165 a VALUES reference to a target column resolved against the table and
    /// then indexed the empty row, and the <see cref="IndexOutOfRangeException"/> ended the wire
    /// session with <c>Internal</c>.
    /// </summary>
    /// <remarks>
    /// The column check runs first, so a reference anywhere in the expression, including under an
    /// aggregate, a sign or a CAST, reports this rule rather than another error.
    /// </remarks>
    /// <param name="expression">The expression to validate.</param>
    /// <param name="clause">The clause, as diagnostics name it.</param>
    /// <param name="alternative">The clause's own way to read table columns, or null when it has none.</param>
    /// <exception cref="SqlEvaluationException">
    /// The expression references a column (<c>COHSQLE005</c>), or signs an operand the plan
    /// already knows is not a number (<c>COHSQLE003</c>).
    /// </exception>
    /// <exception cref="DatabaseException">
    /// The expression contains an aggregate, <c>*</c> or a subquery.
    /// </exception>
    private void ValidateScopelessExpression(SqlExpression expression, string clause, string? alternative = null)
    {
        RejectColumnReferences(expression, clause, alternative);
        if (ContainsAggregate(expression))
        {
            throw new SqlPlanningException("COHSQLE017", $"Aggregate functions are not allowed in {clause}.", expression.Location);
        }
        if (ContainsStar(expression))
        {
            throw new SqlPlanningException("COHSQLE013", $"'*' is not allowed in {clause}.", expression.Location);
        }

        ValidateExpression(expression, ScopelessEvaluator);
    }

    /// <summary>
    /// The empty column scope that VALUES rows and LIMIT/OFFSET counts bind and evaluate against,
    /// created on first use and shared by the statement.
    /// </summary>
    private SqlExpressionEvaluator ScopelessEvaluator => _scopelessEvaluator ??=
        new SqlExpressionEvaluator(Array.Empty<SqlCatalogColumn>(), _parameters, defaultCollation: _catalog.DefaultCollation, functions: _functions, cancellationToken: _cancellationToken);

    /// <summary>Whether <c>*</c> appears anywhere in an expression, outside a subquery.</summary>
    /// <param name="expression">The expression to search.</param>
    /// <returns><see langword="true"/> when the expression contains <c>*</c>.</returns>
    internal static bool ContainsStar(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return expression is SqlStarExpression || Children(expression).Any(ContainsStar);
    }

    /// <summary>Rejects the first column reference in an expression, outside a subquery.</summary>
    /// <param name="expression">The expression to search.</param>
    /// <param name="clause">The clause, as diagnostics name it.</param>
    /// <param name="alternative">The clause's own way to read table columns, or null when it has none.</param>
    /// <exception cref="SqlEvaluationException">The expression references a column (<c>COHSQLE005</c>).</exception>
    internal static void RejectColumnReferences(SqlExpression expression, string clause, string? alternative)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlColumnReferenceExpression column)
        {
            string name = string.Join('.', new[] { column.SchemaName, column.TableAlias, column.ColumnName }
                .Where(part => part is not null));
            throw SqlEvaluationException.ColumnReferenceNotAllowed(name, clause, alternative).At(column.Location);
        }

        foreach (var child in Children(expression))
        {
            RejectColumnReferences(child, clause, alternative);
        }
    }

    /// <summary>Binds an UPDATE: each assignment's target column and value, and the row filter.</summary>
    /// <remarks>
    /// UPDATE writes one row at a time, so no group exists for an aggregate to summarize: an
    /// aggregate in an assignment or the WHERE filter fails while planning, as PostgreSQL's parse
    /// analysis rejects one in <c>EXPR_KIND_UPDATE_SOURCE</c> and <c>EXPR_KIND_WHERE</c>
    /// (<c>check_agglevels_and_constraints</c>, <c>src/backend/parser/parse_agg.c</c>, SQLSTATE
    /// 42803). Without the check the statement succeeded over an empty table and failed per row
    /// over a populated one. The search treats a subquery as opaque, since an aggregate there
    /// summarizes the subquery's own rows. WHERE children bind to typed slots before the DML
    /// filter is validated; subqueries in SET retain their capability refusal.
    /// </remarks>
    /// <exception cref="DatabaseException">An assignment or the filter contains an aggregate.</exception>
    private SqlPlan PlanUpdate(SqlUpdateExpression update)
    {
        var queries = BindDmlSubqueries(update.Where, update.Table);
        var table = ResolveTable(update.Table);
        var evaluator = new SqlExpressionEvaluator(table.Columns, _parameters,
            defaultCollation: _catalog.DefaultCollation, subquerySlots: _subquerySlots, functions: _functions, cancellationToken: _cancellationToken,
            outerScope: OuterScope, outerDependencies: OuterDependencies, relation: update.Table);

        var assignments = new List<(int Ordinal, SqlBoundExpression Value)>(update.Assignments.Count);
        foreach (var assignment in update.Assignments)
        {
            int ordinal = FindColumnOrdinal(table, assignment.ColumnName);
            if (table.Columns[ordinal].Identity is { IsAlways: true })
            {
                throw SqlPlanningException.IdentityViolation(assignment.ColumnName, ErrorMessages.IdentityUpdateForbidden);
            }
            if (assignment.Value is SqlDefaultExpression)
            {
                assignments.Add((ordinal, SqlBoundDefaultItem.Instance));
                continue;
            }
            if (ContainsAggregate(assignment.Value))
            {
                throw new SqlPlanningException("COHSQLE017", "Aggregate functions are not allowed in UPDATE SET.", assignment.Value.Location ?? update.Location);
            }
            ValidateExpression(assignment.Value, evaluator);
            assignments.Add((ordinal, evaluator.Bind(assignment.Value)));
        }

        SqlBoundExpression? where = null;
        if (update.Where is not null)
        {
            RejectAggregateInDmlWhere(update.Where);
            ValidateExpression(update.Where, evaluator, _subqueryTypes);
            where = evaluator.Bind(update.Where);
        }

        var input = new SqlUpdatePlan(table, assignments, where);
        return WrapSubqueries(input, queries);
    }

    /// <summary>Binds a DELETE's row filter.</summary>
    /// <remarks>An aggregate in the filter fails while planning, as in UPDATE (see <see cref="PlanUpdate"/>).</remarks>
    /// <exception cref="DatabaseException">The filter contains an aggregate.</exception>
    private SqlPlan PlanDelete(SqlDeleteExpression delete)
    {
        var queries = BindDmlSubqueries(delete.Where, delete.Table);
        var table = ResolveTable(delete.Table);

        SqlBoundExpression? where = null;
        if (delete.Where is not null)
        {
            RejectAggregateInDmlWhere(delete.Where);
            var evaluator = new SqlExpressionEvaluator(table.Columns, _parameters,
                defaultCollation: _catalog.DefaultCollation, subquerySlots: _subquerySlots, functions: _functions, cancellationToken: _cancellationToken,
                outerScope: OuterScope, outerDependencies: OuterDependencies, relation: delete.Table);
            ValidateExpression(delete.Where, evaluator, _subqueryTypes);
            where = evaluator.Bind(delete.Where);
        }

        var input = new SqlDeletePlan(table, where);
        return WrapSubqueries(input, queries);
    }

    /// <summary>
    /// Rejects an aggregate in an UPDATE or DELETE filter. Unlike a SELECT's WHERE, the message
    /// suggests no HAVING, which these statements do not have.
    /// </summary>
    /// <param name="where">The statement's WHERE filter.</param>
    /// <exception cref="DatabaseException">The filter contains an aggregate.</exception>
    private void RejectAggregateInDmlWhere(SqlExpression where)
    {
        if (ContainsAggregate(where))
        {
            throw new SqlPlanningException("COHSQLE017", "Aggregate functions are not allowed in WHERE.", where.Location);
        }
    }

    private SqlCreateTablePlan PlanCreateTable(SqlCreateTableExpression create)
    {
        string schema = create.Table.SchemaName ?? DefaultSchema;
        var columns = new List<SqlCatalogColumn>(create.Columns.Count);
        var primaryKey = new List<string>();
        bool hasIdentity = false;

        foreach (var definition in create.Columns)
        {
            var type = ResolveColumnType(definition);
            var typeInfo = type.Storage;

            var identity = ResolveIdentity(definition, typeInfo, hasIdentity);
            hasIdentity |= identity is not null;
            string? defaultLiteral = identity is null ? ResolveDefaultLiteral(definition, $"{schema}.{create.Table.TableName}") : null;

            // PRIMARY KEY columns are implicitly NOT NULL.
            bool nullable = definition.IsNullable && !definition.IsPrimaryKey && identity is null;
            columns.Add(new SqlCatalogColumn(definition.ColumnName, typeInfo, nullable, defaultLiteral,
                ResolveColumnCollation(definition, typeInfo), type.IsDomain ? type.Name : null, DefaultCalls(defaultLiteral), identity));

            if (definition.IsPrimaryKey)
            {
                primaryKey.Add(definition.ColumnName);
            }
        }

        foreach (var constraint in create.Constraints.Where(c => c.Kind == SqlConstraintKind.PrimaryKey))
        {
            foreach (string column in constraint.Columns)
            {
                if (!primaryKey.Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    primaryKey.Add(column);
                }
            }
        }
        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (primaryKey.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
            {
                columns[i] = new SqlCatalogColumn(column.Name, column.Type, false, column.DefaultLiteral, column.Collation, column.DomainName, column.DefaultFunctionCalls, column.Identity, column.MissingValueLiteral);
            }
        }
        return new SqlCreateTablePlan(schema, create.Table.TableName, columns, primaryKey, create.IfNotExists, create.Constraints);
    }

    /// <summary>
    /// Rejects unevaluated schema expressions before a DDL plan can mutate the catalog, and
    /// renders a literal DEFAULT as the canonical SQL text the catalog stores.
    /// </summary>
    /// <param name="definition">The parsed column definition.</param>
    /// <param name="table">The schema-qualified table name, for diagnostics.</param>
    /// <returns>The canonical DEFAULT text, or null when the column declares none (or <c>DEFAULT NULL</c>).</returns>
    private string? ResolveDefaultLiteral(SqlColumnDefinition definition, string table)
    {
        var type = ResolveColumnType(definition);
        if (definition.DefaultValue is null)
        {
            return type.DefaultLiteral;
        }
        return ResolveDefaultLiteral(definition.DefaultValue, type, definition.ColumnName, table);
    }

    private string? ResolveDefaultLiteral(SqlExpression expression, SqlType type, string columnName, string table)
    {
        if (expression is SqlLiteralExpression { LiteralType: SqlLiteralType.Null })
        {
            return type.DefaultLiteral is null ? null : "NULL";
        }
        if (expression is SqlLiteralExpression typed)
        {
            DatabaseType? literalType = typed.LiteralType switch
            {
                SqlLiteralType.Date => DatabaseType.Date, SqlLiteralType.Time => DatabaseType.Time,
                SqlLiteralType.Timestamp => DatabaseType.DateTime, SqlLiteralType.TimestampWithTimeZone => DatabaseType.DateTimeOffset,
                SqlLiteralType.Interval => DatabaseType.TimeSpan, SqlLiteralType.Binary => DatabaseType.Binary, _ => null,
            };
            if (literalType is { } declared && declared != type.Storage.Type)
            {
                throw new SqlPlanningException("COHSQLE013", $"Column '{columnName}': typed DEFAULT {typed.LiteralType} does not match {type.Storage.Type}.", typed.Location);
            }
        }
        if (!SqlPersistedExpression.IsDefaultLiteral(expression))
        {
            var scope = new SqlExpressionEvaluator([], null, defaultCollation: _catalog.DefaultCollation, functions: _functions, cancellationToken: _cancellationToken);
            ValidateDefaultSyntax(expression, columnName);
            try
            {
                SqlPersistedExpression.Bind(expression, scope);
                ValidateExpression(expression, scope);
            }
            catch (SqlPlanningException failure)
            {
                throw new SqlPlanningException(failure.Diagnostics[0].Code!, string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.DefaultExpressionFailure, columnName, failure.Message), expression.Location, failure);
            }
            catch (SqlEvaluationException failure)
            {
                throw failure.WithContext(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DefaultExpressionFailure, columnName, "DEFAULT"));
            }
            ValidateDefaultType(expression, scope, type.Storage.Type, columnName);
        }
        return SqlPersistedExpression.Canonicalize(expression, $"DEFAULT of column '{columnName}' on table '{table}'");
    }

    // PostgreSQL heap.c:3409 admits defaults with the same assignment coercion as INSERT.
    internal static void ValidateDefaultType(SqlExpression expression, SqlExpressionEvaluator scope, DatabaseType target, string column)
    {
        var source = scope.StaticTypeOf(expression);
        if (!CanAssignInsertType(source, target) && !(source == DatabaseType.DateTimeOffset && target == DatabaseType.DateTime))
        {
            throw new SqlPlanningException("COHSQLE013", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.DefaultTypeIncompatible, column, source, target), expression.Location);
        }
    }

    private void ValidateDefaultSyntax(SqlExpression expression, string column)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlColumnReferenceExpression or SqlParameterExpression or SqlSubqueryExpression or SqlExistsExpression or SqlStarExpression ||
            expression is SqlFunctionCallExpression call && _functions.Catalog.IsAggregate(call.FunctionName))
        {
            throw new SqlPlanningException("COHSQLE013", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.DefaultVariablesForbidden, column), expression.Location);
        }
        foreach (var child in Children(expression)) { ValidateDefaultSyntax(child, column); }
    }

    private IReadOnlyList<string> DefaultCalls(string? text)
    {
        if (text is null) { return []; }
        var expression = SqlPersistedExpression.Load(text, "DEFAULT");
        var scope = new SqlExpressionEvaluator([], null, defaultCollation: _catalog.DefaultCollation, functions: _functions, cancellationToken: _cancellationToken);
        return SqlDefinitionBinder.FunctionCalls(expression, scope);
    }

    /// <summary>Validates the column override without changing inherited database defaults.</summary>
    private static Collation? ResolveColumnCollation(SqlColumnDefinition definition, DatabaseTypeInfo type)
    {
        if (definition.CollationName is null)
        {
            return null;
        }
        if (type.Type is not (DatabaseType.String or DatabaseType.Character))
        {
            throw new SqlPlanningException("COHSQLE013", $"COLLATE requires a string column; '{definition.ColumnName}' is {type.Type}.");
        }
        return Collation.FromName(definition.CollationName);
    }

    private SqlDropTablePlan PlanDropTable(SqlDropTableExpression drop)
        => new(drop.Table.SchemaName ?? DefaultSchema, drop.Table.TableName, drop.IfExists);

    private SqlCreateIndexPlan PlanCreateIndex(SqlCreateIndexExpression create)
    {
        if (string.IsNullOrWhiteSpace(create.IndexName) || create.IndexName == "?")
        {
            throw new SqlPlanningException("COHSQLE013", "CREATE INDEX requires an index name.", create.Location);
        }

        if (create.Columns.Count == 0)
        {
            throw new SqlPlanningException("COHSQLE013", $"CREATE INDEX '{create.IndexName}' requires at least one key column.", create.Location);
        }

        var table = ResolveTable(create.Table);

        foreach (string column in create.Columns)
        {
            int ordinal = FindColumnOrdinal(table, column);
            if (table.Columns[ordinal].Type.Type is DatabaseType.String or DatabaseType.Character or DatabaseType.Json
                && !(table.Columns[ordinal].Collation ?? _catalog.DefaultCollation).IsIndexBacked)
            {
                throw new SqlPlanningException("COHSQLE013", $"Collation 'invariant' is not index-backed; column '{column}' cannot have an index or indexed constraint.", create.Location);
            }
        }

        return new SqlCreateIndexPlan(table, create.IndexName, create.Columns, create.IsUnique, create.IfNotExists);
    }

    private SqlDropIndexPlan PlanDropIndex(SqlDropIndexExpression drop)
    {
        if (string.IsNullOrWhiteSpace(drop.IndexName) || drop.IndexName == "?")
        {
            throw new SqlPlanningException("COHSQLE013", "DROP INDEX requires an index name.", drop.Location);
        }

        if (drop.Table.TableName == "?")
        {
            throw new SqlPlanningException("COHSQLE013", "DROP INDEX requires the table-qualified form: DROP INDEX <name> ON <table>.", drop.Location);
        }

        return new SqlDropIndexPlan(ResolveTable(drop.Table), drop.IndexName, drop.IfExists);
    }

    private SqlPlan PlanAlterTable(SqlAlterTableExpression alter)
    {
        string schema = alter.Table.SchemaName ?? DefaultSchema;

        return alter.Action switch
        {
            SqlAlterAddColumnAction add => PlanAddColumn(schema, alter.Table.TableName, add),
            SqlAlterDropColumnAction drop => new SqlDropColumnPlan(schema, alter.Table.TableName, drop.ColumnName),
            SqlAlterAddConstraintAction add => new SqlAddConstraintPlan(ResolveTable(alter.Table), add.Constraint),
            SqlAlterDropConstraintAction drop => new SqlDropConstraintPlan(ResolveTable(alter.Table), drop.ConstraintName),
            SqlAlterColumnAction change => PlanAlterColumn(alter.Table, change),
            SqlAlterRenameTableAction rename => new SqlRenamePlan(ResolveTable(alter.Table), null, rename.NewName),
            SqlAlterRenameColumnAction rename => new SqlRenamePlan(ResolveTable(alter.Table), rename.ColumnName, rename.NewName),
            SqlAlterSetDefaultAction change => PlanColumnFacet(alter.Table, change.ColumnName, change.DefaultValue, false, null),
            SqlAlterDropDefaultAction change => PlanColumnFacet(alter.Table, change.ColumnName, null, true, null),
            SqlAlterSetNotNullAction change => PlanColumnFacet(alter.Table, change.ColumnName, null, false, false),
            SqlAlterDropNotNullAction change => PlanColumnFacet(alter.Table, change.ColumnName, null, false, true),
            _ => throw new SqlPlanningException("COHSQLE013", "This ALTER TABLE action is not supported by the executor yet.", alter.Location),
        };
    }

    private SqlAlterColumnPlan PlanColumnFacet(SqlTableReference reference, string name, SqlExpression? value, bool dropDefault, bool? nullable)
    {
        var table = ResolveTable(reference);
        var previous = table.Columns[FindColumnOrdinal(table, name)];
        if (previous.Identity is not null && (value is not null || dropDefault || nullable == true))
        {
            throw SqlPlanningException.IdentityViolation(name, ErrorMessages.IdentityAlterForbidden);
        }
        if (nullable == true && table.PrimaryKeyColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new SqlPlanningException("SQL0003", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ColumnNullabilityConflict, name));
        }
        var type = SqlType.ForStorage(previous.Type);
        if ((value is not null || dropDefault) && previous.DomainName is { } domainName)
        {
            if (!_functions.Types.TryGetDomain(domainName, out var domain))
            {
                throw SqlEvaluationException.UnknownType(domainName, $"Column '{name}'");
            }
            type = domain;
        }
        // Removing the column override re-inherits the domain default (rewriteHandler.c:1311).
        // A NULL override remains explicit when it hides a domain default (heap.c:2466-2475).
        string? text = value is not null
            ? ResolveDefaultLiteral(value, type, name, $"{table.Schema}.{table.Name}")
            : dropDefault ? type.DefaultLiteral : previous.DefaultLiteral;
        return new SqlAlterColumnPlan(table, new SqlCatalogColumn(previous.Name, previous.Type, nullable ?? previous.IsNullable,
            text, previous.Collation, previous.DomainName, value is not null ? DefaultCalls(text) : dropDefault ? [] : previous.DefaultFunctionCalls,
            previous.Identity, previous.MissingValueLiteral), AllowDefaultChange: value is not null || dropDefault);
    }

    private SqlAlterColumnPlan PlanAlterColumn(SqlTableReference reference, SqlAlterColumnAction change)
    {
        var table = ResolveTable(reference);
        if (change.Column.Constraints.Count != 0 || change.Column.IsPrimaryKey)
        {
            throw new SqlPlanningException("COHSQLE013", "ALTER COLUMN does not change constraints or primary keys.");
        }
        var previous = table.Columns[FindColumnOrdinal(table, change.Column.ColumnName)];
        if (change.Column.Identity is not null || previous.Identity is not null &&
            (change.Column.IsNullable || change.Column.DefaultValue is not null))
        {
            throw SqlPlanningException.IdentityViolation(change.Column.ColumnName, ErrorMessages.IdentityAlterForbidden);
        }
        if (change.Column.IsNullable && table.PrimaryKeyColumns.Contains(change.Column.ColumnName, StringComparer.OrdinalIgnoreCase))
        {
            throw new SqlPlanningException("SQL0003", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.ColumnNullabilityConflict, change.Column.ColumnName));
        }
        var type = ResolveColumnType(change.Column);
        string? defaultText = ResolveDefaultLiteral(change.Column, $"{table.Schema}.{table.Name}");
        return new SqlAlterColumnPlan(table, new SqlCatalogColumn(change.Column.ColumnName, type.Storage,
            change.Column.IsNullable, defaultText,
            ResolveColumnCollation(change.Column, type.Storage), type.IsDomain ? type.Name : null,
            DefaultCalls(defaultText), previous.Identity, previous.MissingValueLiteral));
    }

    private SqlAddColumnPlan PlanAddColumn(string schema, string table, SqlAlterAddColumnAction add)
    {
        var type = ResolveColumnType(add.Column);
        if (!_catalog.TryGetTable(schema, table, out var current))
        {
            throw new SqlPlanningException("COHSQLE014", string.Format(CultureInfo.CurrentCulture, ErrorMessages.TableMissing, schema, table));
        }
        var identity = ResolveIdentity(add.Column, type.Storage, current.Columns.Any(column => column.Identity is not null));
        string? defaultLiteral = identity is null ? ResolveDefaultLiteral(add.Column, $"{schema}.{table}") : null;
        return new SqlAddColumnPlan(
            schema,
            table,
            new SqlCatalogColumn(
                add.Column.ColumnName,
                type.Storage,
                add.Column.IsNullable && !add.Column.IsPrimaryKey && identity is null,
                defaultLiteral,
                ResolveColumnCollation(add.Column, type.Storage),
                type.IsDomain ? type.Name : null, DefaultCalls(defaultLiteral), identity: identity),
            add.Column.Constraints);
    }

    private SqlCatalogTable ResolveTable(SqlTableReference reference)
    {
        string schema = reference.SchemaName ?? DefaultSchema;

        if (!_catalog.TryGetTable(schema, reference.TableName, out var table))
        {
            throw new SqlPlanningException("COHSQLE014", string.Format(CultureInfo.CurrentCulture, ErrorMessages.TableMissing, schema, reference.TableName));
        }

        return table;
    }

    private static int FindColumnOrdinal(SqlCatalogTable table, string columnName)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new SqlPlanningException("COHSQLE015", $"Table '{table.Schema}.{table.Name}' has no column named '{columnName}'.");
    }

    /// <summary>
    /// Resolves a DDL type token like <c>VARCHAR(100)</c>, <c>DECIMAL(18, 4)</c> or <c>email</c>: a
    /// built-in type name of the declared dialect first, then a domain of the engine's type catalog
    /// (phase E3), whose column stores its base type.
    /// </summary>
    /// <exception cref="DatabaseException">The arguments are malformed, or a domain is given arguments.</exception>
    /// <exception cref="SqlEvaluationException">The name is neither a built-in type nor a registered domain (<c>COHSQLE010</c>).</exception>
    private SqlType ResolveColumnType(SqlColumnDefinition column)
    {
        if (column.ResolvedType is { } builtIn) { return SqlType.ForStorage(builtIn); }
        string dataType = column.DataType;
        string columnName = column.ColumnName;
        // The parser normalizes the arguments; the catalog guards the planner seam so a malformed
        // argument is a statement error, never a raw FormatException that ends a wire session.
        if (_functions.Types.Resolve(dataType, out string? error) is { } type)
        {
            return type;
        }

        if (error is not null)
        {
            throw new SqlPlanningException("COHSQLE013", $"Column '{columnName}': {error}.");
        }

        throw SqlEvaluationException.UnknownType(dataType, $"Column '{columnName}'");
    }

    /// <summary>
    /// Resolves a CAST's target: the built-in type the parser resolved, with its length, precision
    /// and scale, or a domain of the engine's type catalog (phase E3), which the parser leaves to the
    /// planner.
    /// </summary>
    /// <param name="cast">The CAST.</param>
    /// <param name="types">The engine's type catalog.</param>
    /// <returns>The target type.</returns>
    /// <exception cref="DatabaseException">A domain is given type arguments.</exception>
    /// <exception cref="SqlEvaluationException">The name is neither a built-in type nor a registered domain (<c>COHSQLE010</c>).</exception>
    internal static SqlType ResolveCastTarget(SqlCastExpression cast, SqlTypeCatalog types)
    {
        if (cast.TargetTypeInfo is { } builtIn)
        {
            return SqlType.ForStorage(builtIn);
        }

        if (types.Resolve(cast.TargetType, out string? error) is { } type)
        {
            return type;
        }

        if (error is not null)
        {
            throw new SqlPlanningException("COHSQLE018", $"CAST target '{cast.TargetType}': {error}.", cast.Location);
        }

        throw SqlEvaluationException.UnknownType(cast.TargetType, "CAST target");
    }

    private static string LimitClause(SqlSelectExpression select) => select.LimitSyntaxKind switch
    {
        SqlLimitSyntaxKind.Fetch => "FETCH",
        SqlLimitSyntaxKind.Top => "TOP",
        _ => "LIMIT",
    };

    private long? EvaluateCount(SqlExpression? expression, string clause)
    {
        if (expression is null)
        {
            return null;
        }

        // A count has no columns in scope; a reference fails here as COHSQLE005 (#1165).
        ValidateScopelessExpression(expression, clause);
        if (_commonTableState?.BindingUnused == true)
        {
            _ = ScopelessEvaluator.Bind(expression);
            var type = GroupExpressionType(expression, Array.Empty<SqlCatalogColumn>(), ScopelessEvaluator);
            if (type is not (DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64))
            {
                throw new SqlPlanningException("COHSQLE018", string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.CountNonNegative, clause), expression.Location);
            }
            return null;
        }
        object? value = ScopelessEvaluator.Evaluate(expression, Array.Empty<object?>());

        return value switch
        {
            sbyte number when number >= 0 => number,
            short number when number >= 0 => number,
            long number when number >= 0 => number,
            int number when number >= 0 => number,
            _ => throw new SqlPlanningException("COHSQLE018", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.CountNonNegative, clause), expression.Location),
        };
    }

    /// <summary>
    /// Checks an expression the planner is about to bind.
    /// </summary>
    /// <param name="expression">The expression to validate.</param>
    /// <param name="evaluator">The evaluator resolving column references.</param>
    /// <param name="boundSubqueries">
    /// Subquery nodes this statement has already bound to child plans. A subquery outside
    /// that set sits in a position that does not run <see cref="PlanSubqueries"/> — an
    /// UPDATE SET, VALUES, CHECK, DEFAULT or LIMIT/OFFSET expression — and is rejected. Null
    /// rejects every subquery, which is what the constraint validators want.
    /// </param>
    /// <param name="boundValues">ORDER BY nodes already bound to output values.</param>
    /// <exception cref="DatabaseException">The expression cannot be planned.</exception>
    internal static void ValidateExpression(SqlExpression expression, SqlExpressionEvaluator evaluator,
        IReadOnlyDictionary<SqlExpression, DatabaseType>? boundSubqueries = null,
        IReadOnlyDictionary<SqlExpression, int>? boundValues = null)
    {
        try
        {
            ValidateExpressionCore(expression, evaluator, boundSubqueries, boundValues);
        }
        catch (SqlEvaluationException exception) when (exception.Diagnostics[0].Start is null && expression.Location is not null)
        {
            throw exception.At(expression.Location);
        }
    }

    private static void ValidateExpressionCore(SqlExpression expression, SqlExpressionEvaluator evaluator,
        IReadOnlyDictionary<SqlExpression, DatabaseType>? boundSubqueries,
        IReadOnlyDictionary<SqlExpression, int>? boundValues)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (boundValues is not null && boundValues.ContainsKey(expression))
        {
            return;
        }
        bool isBound = boundSubqueries is not null && boundSubqueries.ContainsKey(expression);
        switch (expression)
        {
            case SqlParameterExpression parameter when !evaluator.TryGetParameterValue(parameter, out _):
                throw new SqlPlanningException("COHSQLE020", $"No value was supplied for parameter '{parameter.ParameterName[1..]}'.", parameter.Location);
            case SqlCollateExpression collate:
                Collation.FromName(collate.CollationName);
                break;
            case SqlCastExpression cast:
                ValidateCast(cast, evaluator, boundSubqueries, boundValues);
                break;
            case SqlSubqueryExpression or SqlExistsExpression when !isBound:
                throw new SqlPlanningException("COHDBL001", "Subqueries are supported only in SELECT expressions and INSERT ... SELECT.", expression.Location);
            case SqlColumnReferenceExpression reference:
                evaluator.ResolveColumn(reference); // resolves innermost first, then enclosing query scopes
                break;
            case SqlInExpression { Values: null } when !isBound:
                throw new SqlPlanningException("COHDBL001", "IN subqueries are supported only in SELECT expressions and INSERT ... SELECT.", expression.Location);
            case SqlUnaryExpression { Operator: SqlUnaryOperator.Negate or SqlUnaryOperator.Plus } sign
                when StaticOperandType(sign.Operand, evaluator, boundSubqueries, boundValues) is { } type && !IsNumeric(type) && !(sign.Operator == SqlUnaryOperator.Negate && type == DatabaseType.TimeSpan):
                // A sign over a value the plan already knows is not a number fails here, the same
                // over an empty table as over a populated one; the evaluator raises the same code
                // for an operand only a row's value reveals.
                throw SqlEvaluationException.InvalidOperandType(
                    sign.Operator == SqlUnaryOperator.Plus ? "+" : "-", type.ToString());
            case SqlUnaryExpression { Operator: SqlUnaryOperator.Negate or SqlUnaryOperator.Plus, Operand: SqlParameterExpression parameter } sign
                when evaluator.TryGetParameterValue(parameter, out object? value) && value is not null &&
                    !SqlExpressionEvaluator.IsSignOperand(sign.Operator, value):
                // A parameter's value is known before any row is read, so its type is checked here
                // too: the statement fails whether or not the table has rows.
                throw SqlEvaluationException.InvalidOperandType(
                    sign.Operator == SqlUnaryOperator.Plus ? "+" : "-", SqlExpressionEvaluator.OperandTypeName(value));
        }

        // A bound subquery is its own scope, already planned and validated on its own terms.
        if (isBound && expression is SqlSubqueryExpression or SqlExistsExpression)
        {
            return;
        }

        foreach (var child in Children(expression))
        {
            ValidateExpression(child, evaluator, boundSubqueries, boundValues);
        }

        if (expression is SqlBinaryExpression { Operator: SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo } arithmetic)
        {
            ValidateTemporalArithmetic(arithmetic, evaluator);
        }

        // After its arguments, as PostgreSQL resolves a call, so an argument's error is reported
        // first. The planner's walk (ValidateFunctionCalls) checked every call's count already; this
        // chooses the overload by the arguments' types, with the scope's columns in hand.
        if (expression is SqlFunctionCallExpression call)
        {
            ResolveCall(call, evaluator);
        }
    }

    internal static void ValidateTemporalArithmetic(SqlBinaryExpression arithmetic, SqlExpressionEvaluator evaluator)
        => ValidateTemporalOperands(arithmetic, arithmetic.Left, arithmetic.Right, evaluator);

    // Check the possible values of alternatives even where projection common-type inference
    // does not run (WHERE, UPDATE and CHECK). Ordinary numeric operands allocate no walk state.
    private static void ValidateTemporalOperands(SqlBinaryExpression arithmetic, SqlExpression leftExpression,
        SqlExpression rightExpression, SqlExpressionEvaluator evaluator)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (ValidateAlternatives(leftExpression, rightExpression, left: true) ||
            ValidateAlternatives(rightExpression, leftExpression, left: false)) { return; }
        var left = evaluator.StaticTypeOf(leftExpression);
        var right = evaluator.StaticTypeOf(rightExpression);
        if ((SqlTemporalArithmetic.IsTemporal(left) || SqlTemporalArithmetic.IsTemporal(right)) &&
            left != DatabaseType.Null && right != DatabaseType.Null &&
            SqlTemporalArithmetic.ResultType(arithmetic.Operator, left, right) == DatabaseType.Null)
        { throw SqlTemporalArithmetic.InvalidOperands(arithmetic.Operator, left, right, arithmetic.Location); }

        bool ValidateAlternatives(SqlExpression expression, SqlExpression other, bool left)
        {
            switch (expression)
            {
                case SqlFunctionCallExpression call when SqlStandardLibrary.IsCoalesce(call.FunctionName):
                    foreach (var argument in call.Arguments) { ValidatePair(argument); }
                    return true;
                case SqlFunctionCallExpression call when call.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase):
                    ValidatePair(call.Arguments[0]);
                    return true;
                case SqlCaseExpression conditional:
                    foreach (var clause in conditional.WhenClauses) { ValidatePair(clause.Result); }
                    if (conditional.ElseResult is not null) { ValidatePair(conditional.ElseResult); }
                    return true;
                case SqlCollateExpression collate:
                    ValidatePair(collate.Operand);
                    return true;
                default:
                    return false;
            }

            void ValidatePair(SqlExpression alternative)
                => ValidateTemporalOperands(arithmetic, left ? alternative : other, left ? other : alternative, evaluator);
        }
    }

    /// <summary>
    /// Checks a <c>CAST</c> while planning, the same over an empty table as over a populated one: its
    /// target resolves (a name the parser does not know is a domain of the engine's type catalog, or
    /// <c>COHSQLE010</c>), and when the plan knows the operand's type, a registered cast converts it
    /// to the target, or the operand has the target's storage type already (<c>COHSQLE011</c>
    /// otherwise, PostgreSQL's "cannot cast type ... to ..." at parse analysis). An operand whose
    /// type only its value reveals, a parameter or NULL, is checked when it is evaluated.
    /// </summary>
    /// <param name="cast">The CAST.</param>
    /// <param name="evaluator">The scope its operand is typed in.</param>
    /// <param name="boundSubqueries">The subqueries the statement bound, with their types.</param>
    /// <param name="boundValues">ORDER BY nodes already bound to output values.</param>
    /// <exception cref="SqlEvaluationException">The target does not resolve, or no cast converts the operand.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ValidateCast(SqlCastExpression cast, SqlExpressionEvaluator evaluator,
        IReadOnlyDictionary<SqlExpression, DatabaseType>? boundSubqueries, IReadOnlyDictionary<SqlExpression, int>? boundValues)
    {
        var types = evaluator.Functions.Types;
        var target = ResolveCastTarget(cast, types);
        // Reuse the binder's CASE, arithmetic and aggregate result inference, while an
        // expression whose result type depends on a parameter still checks at evaluation.
        // PostgreSQL parse_expr.c:2764-2788 validates the transformed operand's type here.
        if (CastTypeDependsOnParameter(cast.Operand, evaluator)) { return; }
        var declared = StaticOperandType(cast.Operand, evaluator, boundSubqueries, boundValues)
            ?? evaluator.StaticTypeOf(cast.Operand);
        if (declared == DatabaseType.Null)
        {
            return;
        }

        var source = declared;
        if (source == target.Storage.Type)
        {
            return;
        }

        var sourceDomain = evaluator.SourceDomain(cast.Operand);
        if (types.ResolveCast(sourceDomain, source, target) is null)
        {
            throw SqlEvaluationException.CannotCast(
                sourceDomain is not null && sourceDomain.Storage.Type == declared ? sourceDomain.Name : SqlType.NameOf(declared), target);
        }
    }

    private static bool CastTypeDependsOnParameter(SqlExpression expression, SqlExpressionEvaluator evaluator)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return expression switch
        {
            SqlParameterExpression => true,
            SqlCollateExpression collate => CastTypeDependsOnParameter(collate.Operand, evaluator),
            SqlUnaryExpression { Operator: SqlUnaryOperator.Plus or SqlUnaryOperator.Negate } sign
                => CastTypeDependsOnParameter(sign.Operand, evaluator),
            SqlBinaryExpression { Operator: SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply
                or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo } arithmetic
                => CastTypeDependsOnParameter(arithmetic.Left, evaluator) || CastTypeDependsOnParameter(arithmetic.Right, evaluator),
            SqlCaseExpression conditional => conditional.WhenClauses.Any(clause => CastTypeDependsOnParameter(clause.Result, evaluator))
                || conditional.ElseResult is not null && CastTypeDependsOnParameter(conditional.ElseResult, evaluator),
            SqlFunctionCallExpression call when evaluator.CallResultDependsOnArguments(call)
                => call.Arguments.Any(argument => CastTypeDependsOnParameter(argument, evaluator)),
            _ => false,
        };
    }

    /// <summary>
    /// Resolves a call to its overload while planning: a call no overload's types accept fails here
    /// with <c>COHSQLE006</c>, and one several accept equally well with <c>COHSQLE008</c>, the same
    /// over an empty table as over a populated one.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="evaluator">The scope its arguments are typed in.</param>
    /// <exception cref="SqlEvaluationException">The call does not resolve to exactly one overload.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ResolveCall(SqlFunctionCallExpression call, SqlExpressionEvaluator evaluator)
    {
        if (SqlStandardLibrary.IsEvaluatorForm(call.FunctionName)) { evaluator.ValidateEvaluatorForm(call); }
        else { evaluator.ResolveFunction(call); }
    }

    /// <summary>
    /// The type a sign's or a CAST's operand has whatever row it is evaluated on, when the plan can
    /// tell: a literal (an integer literal is BIGINT and a fractional one NUMERIC, the values they
    /// bind to), a column, a COLLATE or CAST, a predicate, a concatenation, a scalar call's resolved
    /// result type, or a bound scalar subquery. Null for a parameter or NULL, an ORDER BY value
    /// bound to an output column, or computed shapes (arithmetic, aggregates and CASE) whose richer
    /// binder inference <see cref="ValidateCast"/> uses as a fallback. A sign over a parameter is
    /// checked against its supplied value instead by <see cref="ValidateExpression"/>.
    /// </summary>
    private static DatabaseType? StaticOperandType(SqlExpression expression, SqlExpressionEvaluator evaluator,
        IReadOnlyDictionary<SqlExpression, DatabaseType>? boundSubqueries, IReadOnlyDictionary<SqlExpression, int>? boundValues)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (boundValues is not null && boundValues.ContainsKey(expression))
        {
            return null;
        }

        return expression switch
        {
            SqlLiteralExpression { LiteralType: SqlLiteralType.String } => DatabaseType.String,
            SqlLiteralExpression { LiteralType: SqlLiteralType.Boolean } => DatabaseType.Boolean,
            SqlLiteralExpression { LiteralType: SqlLiteralType.Integer } => DatabaseType.Int64,
            SqlLiteralExpression { LiteralType: SqlLiteralType.Float } => DatabaseType.Decimal,
            SqlLiteralExpression { LiteralType: SqlLiteralType.Date } => DatabaseType.Date,
            SqlLiteralExpression { LiteralType: SqlLiteralType.Time } => DatabaseType.Time,
            SqlLiteralExpression { LiteralType: SqlLiteralType.Timestamp } => DatabaseType.DateTime,
            SqlLiteralExpression { LiteralType: SqlLiteralType.TimestampWithTimeZone } => DatabaseType.DateTimeOffset,
            SqlLiteralExpression { LiteralType: SqlLiteralType.Interval } => DatabaseType.TimeSpan,
            SqlLiteralExpression { LiteralType: SqlLiteralType.Binary } => DatabaseType.Binary,
            SqlColumnReferenceExpression column => evaluator.ResolveColumnType(column),
            SqlCollateExpression collate => StaticOperandType(collate.Operand, evaluator, boundSubqueries, boundValues),
            SqlCastExpression cast => cast.TargetTypeInfo?.Type ?? evaluator.TryResolveCastTarget(cast)?.Storage.Type,
            SqlUnaryExpression { Operator: SqlUnaryOperator.Not } => DatabaseType.Boolean,
            SqlLogicalExpression => DatabaseType.Boolean,
            SqlBinaryExpression { Operator: SqlBinaryOperator.Concat } => DatabaseType.String,
            SqlBinaryExpression { Operator: not (SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply
                or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo) } => DatabaseType.Boolean,
            SqlIsNullExpression or SqlBetweenExpression or SqlInExpression or SqlLikeExpression or SqlExistsExpression => DatabaseType.Boolean,
            SqlSubqueryExpression when boundSubqueries is not null && boundSubqueries.TryGetValue(expression, out var type) => type,
            // A scalar call's resolved result type: UPPER and LOWER take their argument's type, so
            // UPPER over text is text. CAST's binder fallback also types aggregate results.
            SqlFunctionCallExpression call => evaluator.StaticCallType(call, scalarOnly: true) is var result && result != DatabaseType.Null
                ? result
                : null,
            _ => null,
        };
    }

    private static bool IsNumeric(DatabaseType type) => type is DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32
        or DatabaseType.Int64 or DatabaseType.Float32 or DatabaseType.Float64 or DatabaseType.Decimal or DatabaseType.Null;

    /// <summary>Whether a projection or an ordering key of a SELECT calls an aggregate, which makes the SELECT a grouping.</summary>
    private bool ProjectsOrOrdersByAggregate(SqlSelectExpression select)
    {
        for (int index = 0; index < select.Columns.Count; index++)
        {
            if (ContainsAggregate(select.Columns[index].Expression))
            {
                return true;
            }
        }
        for (int index = 0; index < select.OrderBy.Count; index++)
        {
            if (ContainsAggregate(select.OrderBy[index].Expression))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether an expression calls an aggregate of the engine's catalog, outside a subquery.</summary>
    /// <remarks>
    /// The children are walked by index where they are a list, as every operand list and a leaf's
    /// empty sequence are: an instance method passed as a predicate would allocate a delegate on
    /// every call, which an INSERT of parameters pays once per value.
    /// </remarks>
    private bool ContainsAggregate(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlFunctionCallExpression call && _functions.Catalog.IsAggregate(call.FunctionName))
        {
            return true;
        }

        var children = Children(expression);
        if (children is IReadOnlyList<SqlExpression> list)
        {
            for (int index = 0; index < list.Count; index++)
            {
                if (ContainsAggregate(list[index]))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var child in children)
        {
            if (ContainsAggregate(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an expression calls a <see cref="SqlFunctionVolatility.Volatile"/> function, or one
    /// that does not resolve, outside a subquery: its value can change between the plan and a row,
    /// so it never bounds an index seek.
    /// </summary>
    private bool ContainsVolatileCall(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlFunctionCallExpression call && !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName))
        {
            try
            {
                if (ScopelessEvaluator.ResolveFunction(call) is not { Volatility: not SqlFunctionVolatility.Volatile })
                {
                    return true;
                }
            }
            catch (SqlEvaluationException)
            {
                return true; // it fails when evaluated; never a seek bound
            }
        }

        foreach (var child in Children(expression))
        {
            if (ContainsVolatileCall(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Finds conversions even when wrapped in an unsupported DDL default expression.</summary>
    private static bool ContainsCast(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return expression is SqlCastExpression || Children(expression).Any(ContainsCast);
    }

    /// <summary>
    /// The operand nodes of an expression, in source order; a subquery's own query is opaque.
    /// </summary>
    /// <remarks>
    /// Every walker that recurses through these children calls
    /// <see cref="RuntimeHelpers.EnsureSufficientExecutionStack"/> before it descends (#1151). A
    /// parsed tree is at most the engine's configured expression nesting limit deep (256 levels
    /// by default, at most 4096), so the check fails for a statement within a high limit that the
    /// thread has too little stack for, or on a thread created with a small stack, and the
    /// statement then fails with <c>COHSQLE004</c> instead of the process overflowing the stack.
    /// An <c>AND</c> or <c>OR</c> chain yields all of its terms at one level, so a walker iterates
    /// a chain of any length instead of recursing once per term. A new walker follows the same
    /// rule.
    /// <para>
    /// A node with no operands (a literal, parameter, column, <c>*</c> or subquery) returns the
    /// shared empty sequence, and a chain or a call its own operand list, so the walkers that visit
    /// every node of every statement allocate nothing for most of them.
    /// </para>
    /// </remarks>
    internal static IEnumerable<SqlExpression> Children(SqlExpression expression) => expression switch
    {
        SqlLogicalExpression logical => logical.Operands,
        SqlFunctionCallExpression function => function.Arguments,
        SqlCollateExpression or SqlBinaryExpression or SqlUnaryExpression or SqlIsNullExpression or SqlBetweenExpression
            or SqlInExpression or SqlLikeExpression or SqlCaseExpression or SqlCastExpression => Operands(expression),
        _ => [],
    };

    /// <summary>The operands of a node that has some, in source order (<see cref="Children"/>).</summary>
    private static IEnumerable<SqlExpression> Operands(SqlExpression expression)
    {
        switch (expression)
        {
            case SqlCollateExpression collate:
                yield return collate.Operand;
                break;
            case SqlBinaryExpression binary:
                yield return binary.Left;
                yield return binary.Right;
                break;
            case SqlUnaryExpression unary:
                yield return unary.Operand;
                break;
            case SqlIsNullExpression isNull:
                yield return isNull.Operand;
                break;
            case SqlBetweenExpression between:
                yield return between.Operand;
                yield return between.Low;
                yield return between.High;
                break;
            case SqlInExpression inExpression:
                yield return inExpression.Operand;
                if (inExpression.Values is not null)
                {
                    foreach (var value in inExpression.Values)
                    {
                        yield return value;
                    }
                }
                break;
            case SqlLikeExpression like:
                yield return like.Operand;
                yield return like.Pattern;
                break;
            case SqlCaseExpression caseExpression:
                if (caseExpression.Input is not null)
                {
                    yield return caseExpression.Input;
                }
                foreach (var when in caseExpression.WhenClauses)
                {
                    yield return when.Condition;
                    yield return when.Result;
                }
                if (caseExpression.ElseResult is not null)
                {
                    yield return caseExpression.ElseResult;
                }
                break;
            case SqlCastExpression cast:
                yield return cast.Operand;
                break;
        }
    }
}
