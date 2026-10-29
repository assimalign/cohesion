using System;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlExpressionEvaluator
{
    internal DatabaseType WindowType(SqlWindowExpression window)
    {
        string name = window.Function.FunctionName.ToUpperInvariant();
        if (name is "PERCENT_RANK" or "CUME_DIST") { return DatabaseType.Float64; }
        if (name == "NTILE") { return DatabaseType.Int32; }
        if (name is not ("LAG" or "LEAD")) { return DatabaseType.Int64; }
        var args = window.Function.Arguments;
        if (args.Count == 0) { return DatabaseType.Null; }
        var first = StaticTypeOf(args[0]);
        if (args.Count < 3) { return first == DatabaseType.Null ? DatabaseType.String : first; }
        var other = StaticTypeOf(args[2]);
        if (first == DatabaseType.Null) { return other == DatabaseType.Null ? DatabaseType.String : other; }
        if (other == DatabaseType.Null) { return first; }
        bool unknownFirst = args[0] is SqlLiteralExpression { LiteralType: SqlLiteralType.Null or SqlLiteralType.String };
        bool unknownOther = args[2] is SqlLiteralExpression { LiteralType: SqlLiteralType.Null or SqlLiteralType.String };
        if (unknownFirst) { return other == DatabaseType.Null ? DatabaseType.String : other; }
        if (unknownOther || first == other) { return first; }
        if (Category(first) != 0 && Category(first) == Category(other))
        {
            var common = first;
            if (CanConvert(first, other) && !CanConvert(other, first)) { common = other; }
            if (CanConvert(first, common) && CanConvert(other, common)) { return common; }
        }
        throw new SqlPlanningException("CS02069", ErrorMessages.WindowDefaultType, window.Location);

        bool CanConvert(DatabaseType from, DatabaseType to) => from == to ||
            _functions.Types.ResolveCast(null, from, SqlType.ForStorage(new DatabaseTypeInfo(to))) is { Context: SqlCastContext.Implicit };

        static int Category(DatabaseType type) => type switch
        {
            DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 or
                DatabaseType.Decimal or DatabaseType.Float32 or DatabaseType.Float64 => 1,
            DatabaseType.Character or DatabaseType.String => 2,
            DatabaseType.Date or DatabaseType.DateTime or DatabaseType.DateTimeOffset => 3,
            _ => 0,
        };
    }
}
