using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlQueryExecutor
{
    private Task<DatabaseResult> ExecuteCopyAsync(SqlCopyToExpression copy, SqlStatementContext statement,
        Stream? destination, CancellationToken cancellationToken)
    {
        if (destination is null) { throw new SqlExecutionException("CS05094", ErrorMessages.CopyStreamRequired); }
        static string Name(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var functions = _definitions.Functions.At(statement.TransactionStart);
        var collation = _catalog.DefaultCollation;
        string table = Name(copy.Table.SchemaName ?? SqlPlanner.DefaultSchema) + "." + Name(copy.Table.TableName);
        SqlPlan Bind(string projection)
        {
            var syntax = SqlQueryRequest.FromSql("SELECT " + projection + " FROM " + table);
            return new SqlPlanner(_catalog, null, functions, cancellationToken, statement)
                .Plan(syntax.Statement.SqlExpression, statement.Snapshot);
        }
        if (Bind("*") is not SqlSelectPlan source)
        { throw new SqlExecutionException("CK00001", ErrorMessages.CopyPhysicalTableRequired); }
        var columns = copy.Columns.Count == 0 ? source.Table.Columns.Select(column => column.Name).ToArray() : copy.Columns.ToArray();
        var available = source.Table.Columns.ToDictionary(column => column.Name, column => column.Name, StringComparer.OrdinalIgnoreCase);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Length; i++)
        {
            if (!available.TryGetValue(columns[i], out string? column))
            {
                throw new SqlPlanningException("CS01015", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.ColumnNameMissing, source.Table.Schema, source.Table.Name, columns[i]), copy.Location);
            }
            if (!unique.Add(column)) { throw new SqlExecutionException("CS01069", ErrorMessages.CopyColumnInvalid, location: copy.Location); }
            columns[i] = column;
        }
        var forced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string column in copy.Options.ForceQuote)
        {
            if (!unique.Contains(column) || !forced.Add(column)) { throw new SqlExecutionException("CS01069", ErrorMessages.CopyColumnInvalid); }
        }
        // Bind actual CAST expressions: CHAR trimming, JSONB decoding, domains and application
        // casts use exactly SELECT CAST(column AS TEXT)'s resolution and cancellation semantics.
        var syntax = (SqlSelectExpression)SqlQueryRequest.FromSql("SELECT " + string.Join(", ", columns.Select(column =>
            "CAST(" + Name(column) + " AS TEXT)"))).Statement.SqlExpression;
        // Bind to the already resolved immutable table. A second catalog lookup could select
        // a replacement table published between source binding and CAST binding.
        var binder = new SqlExpressionEvaluator(source.Table.Columns, null, defaultCollation: collation,
            functions: functions, cancellationToken: cancellationToken);
        var projections = columns.Select((column, i) => new SqlProjection(column, null, syntax.Columns[i].Expression, DatabaseType.String)
            { Value = binder.Bind(syntax.Columns[i].Expression) }).ToArray();
        var plan = source with { Projections = projections };
        var executor = new SqlPlanExecutor(_storage, _catalog, _indexManager, _definitions);
        return executor.CopyToAsync(plan, copy.Options, destination, statement, cancellationToken);
    }
}
