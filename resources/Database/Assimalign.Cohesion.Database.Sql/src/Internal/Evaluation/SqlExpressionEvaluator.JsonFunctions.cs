using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlExpressionEvaluator
{
    internal SqlType JsonReturning(SqlJsonFunctionExpression json)
        => json.ReturningType is { } builtIn ? SqlType.ForStorage(builtIn)
            : json.Returning is { } name ? _functions.Types.Resolve(name, out _) ?? throw SqlEvaluationException.UnknownType(name, "RETURNING")
            : json.Kind == SqlJsonFunctionKind.Exists ? SqlType.Boolean : json.Kind == SqlJsonFunctionKind.Query ? SqlType.Jsonb : SqlType.Text;

    internal static SqlJsonPath? BindJsonPath(SqlJsonFunctionExpression json, IReadOnlyDictionary<string, object?>? parameters)
    {
        object? path = json.Path switch
        {
            SqlLiteralExpression { LiteralType: SqlLiteralType.String } literal => literal.Value,
            SqlParameterExpression parameter when parameters?.TryGetValue(parameter.ParameterName.TrimStart('@', ':'), out var value) == true => value,
            _ => throw new SqlPlanningException("CS01068", ErrorMessages.JsonPathConstantRequired, json.Path.Location),
        };
        if (path is null) { return null; }
        if (path is SqlCharacter character) { path = character.Text; }
        if (path is not string text) { throw new SqlPlanningException("CS01069", ErrorMessages.JsonFunctionSignature, json.Path.Location); }
        try { return SqlJsonPath.Parse(text); }
        catch (SqlExecutionException failure) { throw failure.At(json.Path.Location); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqlBoundExpression BindJsonFunction(SqlJsonFunctionExpression json, out SqlCollationCandidate collation, bool statisticsInput)
    {
        var path = BindJsonPath(json, _parameters);
        var types = new Dictionary<SqlExpression, DatabaseType>();
        var contextType = JsonOperandType(json.Context, types: types);
        if (contextType is not (DatabaseType.String or DatabaseType.Character or DatabaseType.Json or DatabaseType.JsonBinary or DatabaseType.Null))
        { throw new SqlPlanningException("CS01069", ErrorMessages.JsonFunctionSignature, json.Context.Location); }
        var context = BindJsonOperand(json.Context, out SqlCollationCandidate _, statisticsInput, types);
        var target = JsonReturning(json);
        var casts = _functions.Types.ResolveCasts(null, target);
        if (_bindingContext == SqlBindingContext.IndexPredicate)
        { SqlPartialIndexPredicate.ValidateConversions(casts, target, json, this, json.Location, inputType: DatabaseType.String); }
        var conversion = new SqlBoundCast(new SqlBoundConstant(null), target.Name, target,
            casts, _functions.Database, _defaultCollation, json.Location, DatabaseType.String, assignment: true);
        collation = new SqlCollationCandidate(_defaultCollation, 1);
        var empty = BindDefault(json.EmptyDefault);
        var error = BindDefault(json.ErrorDefault);
        return new SqlBoundJsonFunction(json, context, path, conversion, empty, error);

        SqlBoundExpression? BindDefault(SqlExpression? syntax)
        {
            if (syntax is null) { return null; }
            ValidateDefault(syntax);
            var defaultTypes = new Dictionary<SqlExpression, DatabaseType>();
            var sourceType = JsonOperandType(syntax, types: defaultTypes);
            var value = BindJsonOperand(syntax, out SqlCollationCandidate candidate, statisticsInput, defaultTypes);
            if (target.Storage.Type is DatabaseType.String or DatabaseType.Character && candidate.Collation is { } declared && declared != _defaultCollation)
            { throw new SqlPlanningException("CS01069", ErrorMessages.JsonFunctionSignature, syntax.Location); }
            var conversions = ResolveExpressionCasts(syntax, SourceDomain(syntax), target, syntax.Location);
            if (sourceType is not (DatabaseType.Null or DatabaseType.JsonBinary or DatabaseType.Boolean) && sourceType != target.Storage.Type &&
                conversions[(int)SqlCastConverter.DispatchType(sourceType, sourceType)] is null)
            { throw new SqlPlanningException("CS01069", ErrorMessages.JsonFunctionSignature, syntax.Location); }
            var cast = new SqlBoundCast(value, target.Name, target, conversions,
                _functions.Database, _defaultCollation, syntax.Location, sourceType, assignment: true);
            // PostgreSQL parse_coerce.c:232-360 and clauses.c:4222-4278 leave
            // constrained CoerceToDomain nodes for the selected runtime branch.
            var inputTarget = target.IsDomain ? SqlType.ForStorage(target.Storage) : target;
            var inputCast = target.IsDomain ? new SqlBoundCast(value, target.Name, inputTarget,
                _functions.Types.ResolveCasts(null, inputTarget), _functions.Database, _defaultCollation,
                syntax.Location, sourceType, assignment: true) : cast;
            if (syntax is SqlLiteralExpression { LiteralType: SqlLiteralType.String } literal && target.Storage.Type == DatabaseType.JsonBinary)
            {
                var work = SqlJsonPathExecution.ForExpression(_cancellationToken);
                long reserved = work.Reserved;
                try
                {
                    work.Reserve(literal.Value.Length * 12L + 256);
                    using var document = SqlJsonAccess.ReadExtraction(literal.Value);
                    string normalized = SqlJsonSerialization.Render([document.RootElement], false, work);
                    work.ReserveConversion(target, normalized);
                    var converted = ConvertJsonInput(normalized, inputCast, _cancellationToken);
                    return target.IsDomain ? cast : new SqlBoundConstant(converted);
                }
                finally { work.ReleaseTo(reserved); }
            }
            if (syntax is SqlLiteralExpression { LiteralType: SqlLiteralType.String } textLiteral)
            {
                var work = SqlJsonPathExecution.ForExpression(_cancellationToken);
                long reserved = work.Reserved;
                try
                {
                    work.ReserveConversion(target, textLiteral.Value);
                    var converted = ConvertJsonInput(textLiteral.Value, inputCast, _cancellationToken);
                    return target.IsDomain ? cast : new SqlBoundConstant(converted);
                }
                finally { work.ReleaseTo(reserved); }
            }
            return cast;
        }
        void ValidateDefault(SqlExpression expression)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (expression is SqlColumnReferenceExpression or SqlSubqueryExpression or SqlExistsExpression or SqlWindowExpression
                or SqlQuantifiedComparisonExpression or SqlInExpression { Subquery: not null }
                || expression is SqlFunctionCallExpression call && _functions.Catalog.IsAggregate(call.FunctionName))
            { throw new SqlPlanningException("CS01069", ErrorMessages.JsonFunctionSignature, expression.Location); }
            foreach (var child in SqlPlanner.Children(expression)) { ValidateDefault(child); }
        }
    }

    private object? EvaluateJsonFunction(SqlBoundJsonFunction bound, object?[] row)
    {
        var json = bound.Syntax;
        // PostgreSQL execExpr.c:4762-4789,4817-4865: NULL skips path execution,
        // then undergoes domain coercion. Input conversion precedes path handling.
        var input = EvaluateCore(bound.Context, row);
        if (input is null && !bound.Conversion.Target.IsDomain) { return null; }
        if (input is SqlCharacter character) { input = character.Text; }
        var work = SqlJsonPathExecution.ForExpression(_cancellationToken);
        long reserved = work.Reserved;
        try
        {
            if (input is null) { return NullInput(); }
            work.Reserve(input is byte[] bytes ? bytes.LongLength * 6 + 256 : ((string)input).Length * 12L + 256);
            using var source = SqlJsonAccess.ReadExtraction(input);
            // PostgreSQL transforms the context to transient jsonb before evaluating
            // the path: collapse duplicates, order keys and validate all numbers.
            string normalized = SqlJsonSerialization.Render([source.RootElement], false, work);
            work.Reserve(normalized.Length * 6L + 256);
            using var document = SqlJsonAccess.ReadExtraction(normalized);
            if (bound.Path is null) { return NullInput(); }
            List<JsonElement> items;
            object? result;
            try
            {
                items = bound.Path.Evaluate(document.RootElement, work, json.Kind == SqlJsonFunctionKind.Exists);
                if (json.Kind == SqlJsonFunctionKind.Exists) { return Box(items.Count != 0); }
                if (items.Count == 0) { result = null; }
                else
                {
                    bool wrap = json.Kind == SqlJsonFunctionKind.Query && (json.Wrapper == SqlJsonWrapper.Unconditional
                        || json.Wrapper == SqlJsonWrapper.Conditional && items.Count > 1);
                    if (!wrap && (items.Count != 1 || json.Kind == SqlJsonFunctionKind.Value && items[0].ValueKind is JsonValueKind.Object or JsonValueKind.Array))
                    { throw new SqlExecutionException("CS05093", ErrorMessages.JsonPathCardinality); }
                    result = ConvertResult(items, wrap);
                }
            }
            catch (DatabaseException failure) when (Handles(failure))
            { return Behavior(json.OnError, bound.ErrorDefault, failure); }
            // ERROR ON EMPTY and default evaluation/coercion errors are outside
            // ON ERROR (execExprInterp.c:5047-5089,5266-5304).
            return items.Count == 0 ? Behavior(json.OnEmpty, bound.EmptyDefault,
                new SqlExecutionException("CS02083", ErrorMessages.JsonPathEmpty)) : result;
        }
        catch (SqlExecutionException failure) { throw failure.At(json.Location); }
        finally { work.ReleaseTo(reserved); }

        object? NullInput()
        {
            try { return Convert(null); }
            catch (DatabaseException failure) when (Handles(failure))
            { return Behavior(json.OnError, bound.ErrorDefault, failure); }
        }

        object? ConvertResult(IReadOnlyList<JsonElement> items, bool wrap)
        {
            var target = bound.Conversion.Target.Storage.Type;
            bool jsonTarget = target is DatabaseType.Json or DatabaseType.JsonBinary;
            string? text;
            if (json.Kind == SqlJsonFunctionKind.Value && items[0].ValueKind == JsonValueKind.Null)
            { return Convert(null); }
            if (json.Kind == SqlJsonFunctionKind.Value && (!jsonTarget || bound.Conversion.Target.Check is not null))
            {
                var scalar = items[0];
                text = scalar.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => scalar.GetString(),
                    JsonValueKind.True => bound.Conversion.Target.Check is null ? "t" : "true",
                    JsonValueKind.False => bound.Conversion.Target.Check is null ? "f" : "false",
                    JsonValueKind.Number => SqlJsonSerialization.Number(scalar.GetRawText(), work),
                    _ => scalar.GetRawText(),
                };
            }
            else if (!wrap && json.OmitQuotes && items[0].ValueKind == JsonValueKind.String) { text = items[0].GetString(); }
            else { text = SqlJsonSerialization.Render(items, wrap, work); }
            // json_populate_type uses scalar input conversion; JSON_VALUE does
            // the same I/O conversion even for a numeric-looking JSON string.
            return Convert(text);
        }
        object? Behavior(SqlJsonBehavior behavior, SqlBoundExpression? expression, DatabaseException failure)
            => behavior switch
            {
                SqlJsonBehavior.Error => throw failure,
                SqlJsonBehavior.Default => Default(expression!),
                SqlJsonBehavior.True => _true,
                SqlJsonBehavior.False => _false,
                SqlJsonBehavior.EmptyArray => Convert("[]"),
                SqlJsonBehavior.EmptyObject => Convert("{}"),
                _ => Convert(null),
            };
        object? Default(SqlBoundExpression expression)
        {
            if (expression is SqlBoundCast { SourceType: DatabaseType.Boolean } boolean)
            {
                var booleanValue = EvaluateCore(boolean.Operand, row);
                string? booleanText = booleanValue is bool flag ? bound.Conversion.Target.Storage.Type == DatabaseType.Int32
                    ? flag ? "1" : "0" : flag ? "true" : "false" : null;
                return Convert(booleanText);
            }
            if (expression is not SqlBoundCast { SourceType: DatabaseType.JsonBinary } cast)
            {
                if (expression is SqlBoundCast conversion)
                {
                    var defaultInput = EvaluateCore(conversion.Operand, row);
                    if (defaultInput is not null && conversion.Target.Storage.Type == DatabaseType.JsonBinary &&
                        conversion.SourceType is DatabaseType.String or DatabaseType.Character or DatabaseType.Json)
                    {
                        string raw = defaultInput is SqlCharacter fixedText ? fixedText.Text : (string)defaultInput;
                        work.Reserve(raw.Length * 12L + 256);
                        using var parsed = SqlJsonAccess.ReadExtraction(raw);
                        defaultInput = SqlJsonSerialization.Render([parsed.RootElement], false, work);
                    }
                    work.ReserveConversion(conversion.Target, defaultInput);
                    return SqlBinaryValue.CopyShared(ConvertJsonInput(defaultInput, conversion, _cancellationToken));
                }
                var result = EvaluateCore(expression, row);
                work.ReserveConversion(bound.Conversion.Target, result);
                return SqlBinaryValue.CopyShared(result);
            }
            var value = EvaluateCore(cast.Operand, row);
            if (value is null) { return Convert(null); }
            work.Reserve(value is byte[] binary ? binary.LongLength * 6 + 256 : ((string)value).Length * 12L + 256);
            using var source = SqlJsonAccess.ReadExtraction(value);
            bool coercion = cast.Target.Storage.Type != DatabaseType.JsonBinary || cast.Target.IsDomain;
            string text = coercion && (json.Kind == SqlJsonFunctionKind.Value || json.OmitQuotes) && source.RootElement.ValueKind == JsonValueKind.String
                ? source.RootElement.GetString()! : SqlJsonSerialization.Render([source.RootElement], false, work);
            return Convert(text);
        }
        object? Convert(string? text)
        {
            work.ReserveConversion(bound.Conversion.Target, text);
            return ConvertJsonInput(text, bound.Conversion, _cancellationToken);
        }
        static bool Handles(DatabaseException failure)
        {
            foreach (var diagnostic in failure.Diagnostics)
            {
                if (diagnostic.Code == "CS05093") { return true; }
                if (diagnostic.Code?.StartsWith("CS02", StringComparison.Ordinal) == true || diagnostic.Code == "CS03038") { return true; }
            }
            return false;
        }
    }

    private static object? ConvertJsonInput(object? value, SqlBoundCast cast, System.Threading.CancellationToken token)
    {
        if (value is string or SqlCharacter && cast.Target.Storage.Type is
            DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 &&
            cast.Casts[(int)(value is SqlCharacter ? DatabaseType.Character : DatabaseType.String)] is { } integerCast && SqlStandardCasts.Contains(integerCast))
        {
            // json_populate_type and VALUE's I/O route call integer input, which
            // rejects decimal/exponent spelling even when the value is integral
            // (numutils.c:422-467; execExprInterp.c:4998-5039).
            string text = value is SqlCharacter character ? character.Text : (string)value;
            if (text.IndexOfAny(['.', 'e', 'E']) >= 0) { throw InvalidJsonInput(cast); }
        }
        // PostgreSQL bool.c:29-116 accepts unique prefixes and 1/0. SQL/JSON's
        // own boolout also produces t/f. Preserve application cast overrides.
        if (value is string or SqlCharacter && cast.Target.Storage.Type == DatabaseType.Boolean &&
            cast.Casts[(int)(value is SqlCharacter ? DatabaseType.Character : DatabaseType.String)] is { } conversion && SqlStandardCasts.Contains(conversion))
        {
            string text = value is SqlCharacter character ? character.Text : (string)value;
            string input = text.Trim(' ', '\t', '\r', '\n', '\v', '\f');
            bool yes = input == "1" || Prefix("true") || Prefix("yes") || input.Equals("on", StringComparison.OrdinalIgnoreCase);
            bool no = input == "0" || Prefix("false") || Prefix("no") || input.Length >= 2 && Prefix("off");
            if (!yes && !no)
            { throw InvalidJsonInput(cast); }
            string normalized = yes ? "true" : "false";
            value = value is SqlCharacter ? new SqlCharacter(normalized) : normalized;
            bool Prefix(string word) => input.Length > 0 && word.StartsWith(input, StringComparison.OrdinalIgnoreCase);
        }
        return SqlCastConverter.Convert(value, cast, token);
    }

    private static SqlEvaluationException InvalidJsonInput(SqlBoundCast cast)
    {
        var failure = new InvalidCastException(ErrorMessages.SqlStandardCastsCreateInvalidCast);
        return SqlEvaluationException.CastValueFailed(string.Format(CultureInfo.CurrentCulture,
            ErrorMessages.SqlCastConverterConvertSqlEvaluation, SqlType.NameOf(DatabaseType.String), cast.TargetText, failure.Message), failure);
    }
}
