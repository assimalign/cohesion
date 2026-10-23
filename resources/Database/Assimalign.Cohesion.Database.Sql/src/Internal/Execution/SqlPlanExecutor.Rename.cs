using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async Task<DatabaseResult> ExecuteRenameAsync(SqlRenamePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (plan.ColumnName is null && SqlSystemViews.Relations.Any(view =>
            string.Equals(view.Schema, plan.Table.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(view.Name, plan.NewName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SqlPlanningException("CS04021", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExecutorExecuteRenameAsyncSqlPlanning, plan.Table.Schema, plan.NewName));
        }
        await AcquireObjectLockAsync(statement, plan.Table.Schema, plan.Table.Name, "ALTER TABLE RENAME", cancellationToken).ConfigureAwait(false);
        EnsureCurrentDefinition(plan.Table);
        EnsureNoViewReads(plan.Table, plan.ColumnName, rename: true);
        if (plan.ColumnName is not null)
        {
            FindColumnOrdinal(plan.Table, plan.ColumnName);
            if (plan.Table.FindColumn(plan.NewName) is not null)
            {
                throw new SqlPlanningException("CS05013", string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.RenameColumnCollision, plan.NewName, plan.Table.Name));
            }
        }
        else if (_catalog.TryGetTable(plan.Table.Schema, plan.NewName, out _) || _catalog.TryGetView(plan.Table.Schema, plan.NewName, out _))
        {
            throw new SqlPlanningException("CS05013", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.RenameTableCollision, plan.Table.Schema, plan.NewName));
        }
        if (plan.ColumnName is null)
        {
            // Forward-only script receipts retain authored targets; changing those historical
            // claims would invalidate checksum/target verification on a later Build.
            var claims = _catalog.GetIndexes(plan.Table.ObjectId).Select(index => SqlObjectIdentity.Index(plan.Table.Schema, plan.Table.Name, index.Name))
                .Append(SqlObjectIdentity.Table(plan.Table.Schema, plan.Table.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dependent = _catalog.Scripts.FirstOrDefault(script => script.Objects.Any(claims.Contains));
            if (dependent is not null)
            {
                throw new SqlPlanningException("CS03302", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExecutorExecuteRenameAsyncSqlPlanning2, plan.Table.Name, dependent.Name));
            }
        }

        // A new incoming FK must acquire this parent's exclusive lock before publication, so
        // the set is stable now. Lock children by identity, including ownership and stale-definition checks.
        var children = IncomingReferences(plan.Table).Select(reference => reference.Child)
            .Where(child => child.ObjectId != plan.Table.ObjectId).DistinctBy(child => child.ObjectId)
            .OrderBy(child => child.ObjectId).ToArray();
        foreach (var child in children)
        {
            await AcquireObjectLockAsync(statement, child.Schema, child.Name, "ALTER TABLE RENAME dependency", cancellationToken).ConfigureAwait(false);
            EnsureCurrentDefinition(child);
        }
        EnsureCurrentDefinition(plan.Table);
        var checks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var constraint in plan.Table.Constraints.Where(constraint => constraint.Kind == SqlCatalogConstraintKind.Check))
        {
            checks.Add(constraint.Name, RewriteRenameCheck(plan, constraint));
        }
        string RenameColumn(string name) => plan.ColumnName is not null && string.Equals(name, plan.ColumnName, StringComparison.OrdinalIgnoreCase) ? plan.NewName : name;
        var columns = plan.Table.Columns.Select(column => new SqlCatalogColumn(RenameColumn(column.Name), column.Type,
            column.IsNullable, column.DefaultLiteral, column.Collation, column.DomainName, column.DefaultFunctionCalls, column.Identity, column.MissingValueLiteral)).ToArray();
        // Bind rewritten definitions before any mutation. Renaming never re-resolves a recorded call.
        foreach (var constraint in plan.Table.Constraints.Where(constraint => constraint.Kind == SqlCatalogConstraintKind.Check))
        {
            var predicate = SqlPersistedExpression.Load(checks[constraint.Name], $"CHECK '{constraint.Name}'");
            var provisional = new SqlCatalogTable(plan.Table.ObjectId, plan.Table.Schema,
                plan.ColumnName is null ? plan.NewName : plan.Table.Name, columns,
                plan.Table.PrimaryKeyColumns.Select(RenameColumn).ToArray(), plan.Table.Owner, plan.Table.OwningSchema,
                droppedColumnOrdinals: plan.Table.DroppedColumnOrdinals);
            BindPersistedCheck(predicate, provisional, _catalog.DefaultCollation, _definitions.Functions, constraint.CheckFunctionCalls, cancellationToken);
        }
        var publication = statement.ScriptReceipt;
        var updated = await SelfCommitAsync(statement, plan.ColumnName is null
            ? publication is null ? _catalog.RenameTableAsync(plan.Table, plan.NewName, checks, cancellationToken)
                : _catalog.RenameTableAsync(plan.Table, plan.NewName, checks, publication, cancellationToken)
            : publication is null ? _catalog.RenameColumnAsync(plan.Table, plan.ColumnName, plan.NewName, checks, cancellationToken)
                : _catalog.RenameColumnAsync(plan.Table, plan.ColumnName, plan.NewName, checks, publication, cancellationToken)).ConfigureAwait(false);
        List<Exception>? failures = null;
        foreach (var table in children.Prepend(updated))
        {
            try
            {
                // The catalog republishes each incoming FK owner in the same commit.
                _catalog.TryGetTable(table.Schema, table.Name, out var published);
                _definitions.Get(published, CancellationToken.None);
            }
            catch (Exception failure)
            {
                // Every published version needs binding even if an earlier one failed.
                (failures ??= []).Add(failure);
            }
        }
        if (failures is not null) { throw new AggregateException(failures); }
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }

    private static string RewriteRenameCheck(SqlRenamePlan plan, SqlCatalogConstraint constraint)
    {
        // Re-lex only column-reference nodes in the CHECK's single-table scope. Names used as
        // functions, types, collations or literal contents are never candidates for replacement.
        const string prefix = "SELECT * FROM __cohesion_expression WHERE ";
        string source = prefix + constraint.CheckExpression!;
        var select = (SqlSelectExpression)SqlQueryRequest.FromSql(source, null, SqlQueryRequest.CeilingParserOptions).Statement.SqlExpression;
        var replacements = new SortedDictionary<int, (int Length, string Text)>();
        Visit(select.Where!);
        var result = new StringBuilder(source);
        foreach (var (start, replacement) in replacements.Reverse())
        {
            result.Remove(start, replacement.Length).Insert(start, replacement.Text);
        }
        return SqlPersistedExpression.Canonicalize(SqlPersistedExpression.Load(result.ToString(prefix.Length, result.Length - prefix.Length),
            $"CHECK '{constraint.Name}'"), $"CHECK '{constraint.Name}'");

        void Visit(SqlExpression expression)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (expression is SqlColumnReferenceExpression column)
            {
                int start = column.Location!.Start;
                var lexer = new TokenLexer(source.AsSpan(start), SqlLanguageProfile.Instance.ToLexerOptions());
                int components = column.SchemaName is not null ? 3 : column.TableAlias is not null ? 2 : 1;
                for (int component = 0; component < components; component++)
                {
                    lexer.MoveNext();
                    var token = lexer.Current;
                    bool rename = plan.ColumnName is null
                        ? component == components - 2 && string.Equals(column.TableAlias, plan.Table.Name, StringComparison.OrdinalIgnoreCase)
                        : component == components - 1 && string.Equals(column.ColumnName, plan.ColumnName, StringComparison.OrdinalIgnoreCase);
                    if (rename)
                    {
                        replacements[start + token.Position] = (token.Value.Length, "\"" + plan.NewName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
                    }
                    if (component + 1 < components) { lexer.MoveNext(); } // dot
                }
            }
            foreach (var child in SqlPlanner.Children(expression)) { Visit(child); }
        }
    }
}
