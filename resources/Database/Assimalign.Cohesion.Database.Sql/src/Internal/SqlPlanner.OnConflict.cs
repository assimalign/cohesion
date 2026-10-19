using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private SqlOnConflictPlan? BindOnConflict(SqlInsertExpression insert, SqlCatalogTable table)
    {
        if (insert.OnConflict is not { } clause) { return null; }
        var indexes = _catalog.GetIndexes(table.ObjectId);
        var arbiters = new List<SqlCatalogIndex>();
        string target = clause.TargetKind == SqlOnConflictTargetKind.Constraint
            ? clause.ConstraintName! : "(" + string.Join(", ", clause.Columns) + ")";
        if (clause.TargetKind == SqlOnConflictTargetKind.None)
        {
            if (clause.Action == SqlOnConflictAction.DoUpdate) { throw Invalid(ErrorMessages.UpsertTargetRequired); }
            arbiters.AddRange(indexes.Where(index => index.IsUnique));
        }
        else if (clause.TargetKind == SqlOnConflictTargetKind.Constraint)
        {
            if (!_catalog.TryGetIndex(table.ObjectId, clause.ConstraintName!, out var index) || !index.IsUnique)
            {
                string reason = table.Constraints.Any(constraint => string.Equals(constraint.Name, clause.ConstraintName, StringComparison.OrdinalIgnoreCase))
                    ? ErrorMessages.UpsertConstraintNotUnique : IsLegacyPrimaryKey(clause.ConstraintName!)
                        ? ErrorMessages.UpsertLegacyPrimaryKey : ErrorMessages.UpsertConstraintMissing;
                throw Invalid(reason);
            }
            arbiters.Add(index);
        }
        else
        {
            var columns = new HashSet<string>(clause.Columns, StringComparer.OrdinalIgnoreCase);
            foreach (string column in columns)
            {
                if (!table.Columns.Any(candidate => string.Equals(candidate.Name, column, StringComparison.OrdinalIgnoreCase)))
                { throw Invalid(ErrorMessages.UpsertIndexMissing); }
            }
            arbiters.AddRange(indexes.Where(index => index.IsUnique && columns.SetEquals(index.ColumnNames)));
            if (arbiters.Count == 0) { throw Invalid(ErrorMessages.UpsertIndexMissing); }
        }

        var source = new SqlRelationSource(table);
        var evaluator = new SqlExpressionEvaluator(table.Columns.Concat(table.Columns).ToArray(), _parameters,
            [new(source, insert.Table, 0), new(source, clause.Excluded, table.Columns.Count)],
            defaultCollation: _catalog.DefaultCollation, functions: _functions, cancellationToken: _cancellationToken,
            foldConstants: false);
        var assignments = new List<(int Ordinal, SqlBoundExpression Value)>();
        foreach (var assignment in clause.Assignments)
        {
            int ordinal = FindColumnOrdinal(table, assignment.ColumnName);
            if (table.Columns[ordinal].Identity is { IsAlways: true })
            { throw SqlPlanningException.IdentityViolation(assignment.ColumnName, ErrorMessages.IdentityUpdateForbidden); }
            if (assignment.Value is SqlDefaultExpression) { assignments.Add((ordinal, SqlBoundDefaultItem.Instance)); continue; }
            Validate(assignment.Value);
            assignments.Add((ordinal, evaluator.Bind(assignment.Value)));
        }
        if (clause.Where is not null) { Validate(clause.Where); }
        return new(arbiters, indexes.Where(index => index.IsUnique).ToArray(), clause.Action == SqlOnConflictAction.DoUpdate, assignments,
            clause.Where is null ? null : evaluator.Bind(clause.Where));

        SqlPlanningException Invalid(string reason) => new("COHSQLE040",
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.UpsertArbiterInvalid, target, table.Schema, table.Name, reason), insert.Location);

        bool IsLegacyPrimaryKey(string name)
        {
            if (table.PrimaryKeyColumns.Count == 0 || indexes.Any(index => index.IsPrimaryKey)) { return false; }
            // Match the synthetic name reported by SystemKeyConstraints, including its collision suffix.
            var names = new HashSet<string>(indexes.Select(index => index.Name), StringComparer.OrdinalIgnoreCase);
            names.UnionWith(table.Constraints.Select(constraint => constraint.Name));
            string baseName = "PrimaryKey_" + table.Name;
            string candidate = baseName;
            for (int suffix = 1; names.Contains(candidate); suffix++)
            { candidate = baseName + "_" + suffix.ToString(CultureInfo.InvariantCulture); }
            return string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase);
        }

        void Validate(SqlExpression expression)
        {
            if (ContainsAggregate(expression))
            { throw new SqlPlanningException("COHSQLE017", ErrorMessages.UpsertAggregate, expression.Location); }
            if (ContainsStar(expression))
            { throw new SqlPlanningException("COHSQLE013", ErrorMessages.UpsertStar, expression.Location); }
            ValidateExpression(expression, evaluator);
        }
    }
}
