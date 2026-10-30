using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Column defaults early-bind a constant sequence reference, like PostgreSQL's regclass
// dependency. The existing persisted call-identity vector carries S:<object id>.
internal static class SqlSequenceDefault
{
    internal static bool IsCandidate(SqlExpression expression)
        => expression is SqlNextValueExpression || expression is SqlFunctionCallExpression call &&
            call.FunctionName.Equals("NEXTVAL", StringComparison.OrdinalIgnoreCase) && call.Arguments.Count == 1 &&
            call.Quantifier == SqlSetQuantifier.Default && call.OrderBy.Count == 0 && call.Filter is null;

    internal static bool TryId(SqlExpression expression, out ulong id)
    {
        id = 0;
        return expression is SqlFunctionCallExpression call && IsCandidate(expression) &&
            call.Arguments[0] is SqlLiteralExpression { LiteralType: SqlLiteralType.Integer } literal &&
            ulong.TryParse(literal.Value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
    }

    internal static SqlCatalogSequence Resolve(SqlCatalog catalog, SqlExpression expression)
    {
        if (expression is SqlNextValueExpression next)
        { return catalog.FindSequence(next.Sequence.SchemaName ?? SqlPlanner.DefaultSchema, next.Sequence.TableName) ?? throw new SqlPlanningException("CS03073", ErrorMessages.SequenceMissing); }
        var operand = ((SqlFunctionCallExpression)expression).Arguments[0];
        while (operand is SqlCastExpression cast && cast.TargetType.Equals("REGCLASS", StringComparison.OrdinalIgnoreCase)) { operand = cast.Operand; }
        if (operand is not SqlLiteralExpression literal) { throw new SqlPlanningException("CS03074", ErrorMessages.SequencePersistedUnsupported); }
        if (literal.LiteralType == SqlLiteralType.Integer)
        {
            if (!ulong.TryParse(literal.Value, out ulong id)) { throw new SqlPlanningException("CS03077", ErrorMessages.SequenceArgumentsInvalid); }
            return catalog.FindSequence(id) ?? throw new SqlPlanningException("CS03073", ErrorMessages.SequenceMissing);
        }
        if (literal.LiteralType != SqlLiteralType.String) { throw new SqlPlanningException("CS03077", ErrorMessages.SequenceArgumentsInvalid); }
        if (!SqlSequenceExecution.TryName(literal.Value, out string schema, out string name)) { throw new SqlPlanningException("CS03077", ErrorMessages.SequenceArgumentsInvalid); }
        return catalog.FindSequence(schema, name) ?? throw new SqlPlanningException("CS03073", ErrorMessages.SequenceMissing);
    }

    // DDL and reopen must validate the same S:id vector without treating it as an F: call.
    internal static ulong ValidateIdentity(SqlExpression expression, IReadOnlyList<string> identities)
    {
        if (identities.Count != 1 || !identities[0].StartsWith("S:", StringComparison.Ordinal) ||
            !ulong.TryParse(identities[0].AsSpan(2), out ulong sequenceId))
        { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
        if (expression is not SqlNextValueExpression && (!TryId(expression, out ulong encodedId) || encodedId != sequenceId))
        { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
        return sequenceId;
    }

    internal static string Dependency(ulong id) => "S:" + id.ToString(CultureInfo.InvariantCulture);
    internal static void RefuseUnsupported(SqlExpression? expression)
    { if (expression is not null && !IsCandidate(expression)) { SqlSequenceExecution.RefusePersisted(expression); } }
}
