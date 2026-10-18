using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Stateless, reflection-free leaves of the standard scalar library.</summary>
internal sealed class SqlBuiltinScalarFunction : SqlScalarFunction
{
    internal SqlBuiltinScalarFunction(string name, SqlType[] parameters, SqlType result,
        SqlFunctionVolatility volatility = SqlFunctionVolatility.Immutable,
        SqlNullBehavior nullBehavior = SqlNullBehavior.ReturnsNullOnNullInput, SqlType? variadic = null)
        : base(name, parameters, result, volatility, nullBehavior, variadic)
    {
        Usage = name + "(" + string.Join(", ", parameters.Select(type => type == SqlType.AnyElement
                ? name == "DATE_TRUNC" ? "temporal" : name == "ROUND" && parameters.Length == 2 ? "exact numeric" : "numeric"
                : type == SqlType.AnyNumeric ? "numeric" : type.Name)) +
            (variadic is null ? "" : ", " + variadic.Name + " ...") + ")";
        IsNeverNull = parameters.Length == 0 || name == "CONCAT";
        ResultRule = result.IsPseudo ? SqlResultRule.Builtin : SqlResultRule.Declared;
    }

    internal bool AcceptsTypes(ReadOnlySpan<DatabaseType> types)
    {
        if (Name == "DATE_TRUNC")
        {
            return types[1] is DatabaseType.Null or DatabaseType.Date or DatabaseType.DateTime or DatabaseType.DateTimeOffset or DatabaseType.Time or DatabaseType.TimeSpan;
        }
        if (ReturnType.IsPseudo)
        {
            if (types[0] != DatabaseType.Null && !SqlFunctionResolver.IsNumeric(types[0])) { return false; }
            if (Name == "ROUND" && types.Length == 2 && types[0] is DatabaseType.Float32 or DatabaseType.Float64) { return false; }
        }
        return true;
    }

    internal DatabaseType ResultType(ReadOnlySpan<DatabaseType> types)
    {
        if (!ReturnType.IsPseudo) { return ReturnType.Storage.Type; }
        if (Name == "DATE_TRUNC") { return types[1]; }
        if (Name == "MOD" && types[0] is DatabaseType.Float32 or DatabaseType.Float64) { return DatabaseType.Float64; }
        if (Name == "POWER" && types[0] is DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 && types[1] is DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64) { return DatabaseType.Decimal; }
        if (Name == "POWER" && types[1] is not (DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64)) { return DatabaseType.Float64; }
        if (Name == "MOD" && types.Length > 1 && types[1] is DatabaseType.Decimal or DatabaseType.Float32 or DatabaseType.Float64)
        {
            return types[1] == DatabaseType.Decimal && types[0] is not (DatabaseType.Float32 or DatabaseType.Float64) ? DatabaseType.Decimal : DatabaseType.Float64;
        }
        return types[0] switch
        {
            DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 => DatabaseType.Int64,
            DatabaseType.Null => DatabaseType.Decimal,
            _ => types[0],
        };
    }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        try { return SqlValue.FromObject(Compute(in arguments)); }
        catch (ArithmeticException exception) { throw SqlEvaluationException.FromArithmetic(exception); }
        catch (ArgumentOutOfRangeException exception) { throw SqlEvaluationException.NumericValueOutOfRange(Name + " result is out of range.", exception); }
    }

    private object? Compute(scoped in SqlArguments args)
    {
        if (Volatility == SqlFunctionVolatility.Stable && args.Context.TransactionStart is null)
        {
            throw new SqlPlanningException("COHDBL001", "Datetime value functions require an executing SQL transaction; datetime DEFAULT expressions are not supported yet.");
        }
        switch (Name)
        {
            case "CURRENT_DATE": return DateOnly.FromDateTime(args.Context.TransactionStart!.Value.UtcDateTime);
            case "CURRENT_TIME": case "LOCALTIME": return TimeOnly.FromDateTime(args.Context.TransactionStart!.Value.UtcDateTime);
            case "CURRENT_TIMESTAMP": case "NOW": return args.Context.TransactionStart!.Value.ToUniversalTime();
            case "LOCALTIMESTAMP": return DateTime.SpecifyKind(args.Context.TransactionStart!.Value.UtcDateTime, DateTimeKind.Unspecified);
            case "GEN_RANDOM_UUID": return Guid.NewGuid();
            case "DATE_TRUNC": return SqlTemporalFunctions.Truncate(args.GetString(0), args[1].ToObject()!);
            case "CONCAT": case "CONCAT_WS":
                if (Name == "CONCAT_WS" && args.IsNull(0)) { return null; }
                var pieces = new List<string>();
                for (int index = Name == "CONCAT_WS" ? 1 : 0; index < args.Count; index++)
                {
                    if (!args.IsNull(index)) { pieces.Add(SqlCastText.Format(args[index].ToObject()!)); }
                }
                return string.Join(Name == "CONCAT_WS" ? args.GetString(0) : "", pieces);
            case "CHAR_LENGTH": case "CHARACTER_LENGTH": return (long)Runes(args.GetString(0)).Length;
            case "LTRIM": case "RTRIM": case "TRIM":
                var text = Runes(args.GetString(0));
                var trim = Runes(args.Count > 1 ? args.GetString(1) : " ").ToHashSet();
                int start = 0, end = text.Length;
                if (Name != "RTRIM") { while (start < end && trim.Contains(text[start])) { start++; } }
                if (Name != "LTRIM") { while (end > start && trim.Contains(text[end - 1])) { end--; } }
                return Join(text.AsSpan(start, end - start));
            case "REPLACE": return args.GetString(1).Length == 0 ? args.GetString(0) : args.GetString(0).Replace(args.GetString(1), args.GetString(2), StringComparison.Ordinal);
            case "REVERSE":
                var reversed = Runes(args.GetString(0)); Array.Reverse(reversed); return Join(reversed);
            case "POSITION": return Position(args.GetString(1), args.GetString(0));
            case "STRPOS": return Position(args.GetString(0), args.GetString(1));
            case "SUBSTRING": case "LEFT": case "RIGHT":
                var source = Runes(args.GetString(0));
                long offset = 0, length = source.Length;
                long count = args.GetInt64(1);
                if (Name == "SUBSTRING")
                {
                    offset = Math.Max(1, count) - 1;
                    if (args.Count == 3)
                    {
                        length = args.GetInt64(2);
                        if (length < 0) { throw SqlEvaluationException.NumericValueOutOfRange("SUBSTRING length must not be negative."); }
                        if (count < 1)
                        {
                            long omitted = count < 1 - long.MaxValue ? long.MaxValue : 1 - count;
                            length = length <= omitted ? 0 : length - omitted;
                        }
                    }
                }
                else
                {
                    length = count < 0 ? Math.Max(0, source.Length + count) : Math.Min(count, source.Length);
                    if (Name == "RIGHT") { offset = source.Length - length; }
                }
                offset = Math.Min(offset, source.Length);
                length = Math.Min(length, source.Length - offset);
                return Join(source.AsSpan((int)offset, (int)length));
            case "LPAD": case "RPAD":
                var original = Runes(args.GetString(0));
                long requested = args.GetInt64(1);
                if (requested <= 0) { return ""; }
                if (requested > int.MaxValue) { throw SqlEvaluationException.NumericValueOutOfRange(Name + " length is too large."); }
                int target = (int)requested;
                if (target <= original.Length) { return Join(original.AsSpan(0, target)); }
                var padding = Runes(args.Count == 3 ? args.GetString(2) : " ");
                if (padding.Length == 0) { return args.GetString(0); }
                var builder = new StringBuilder();
                if (Name == "RPAD") { builder.Append(args.GetString(0)); }
                for (int index = 0; index < target - original.Length; index++) { builder.Append(padding[index % padding.Length].ToString()); }
                if (Name == "LPAD") { builder.Append(args.GetString(0)); }
                return builder.ToString();
        }
        return Numeric(in args);
    }

    private object Numeric(scoped in SqlArguments args)
    {
        object first = args[0].ToObject()!;
        double x = Convert.ToDouble(first, CultureInfo.InvariantCulture);
        double y = args.Count > 1 ? Convert.ToDouble(args[1].ToObject(), CultureInfo.InvariantCulture) : 0;
        if (Name is "SQRT" or "LN" or "LOG10" && (Name == "SQRT" ? x < 0 : x <= 0) || Name == "POWER" && x == 0 && y < 0)
        {
            throw SqlEvaluationException.NumericValueOutOfRange(Name + " argument is outside its domain.");
        }
        if (Name == "MOD" && y == 0) { throw SqlEvaluationException.DivisionByZero("MOD"); }
        if (Name == "SIGN") { return (long)Math.Sign(x); }
        if (Name is "SQRT" or "EXP" or "LN" or "LOG10")
        {
            return Finite(Name switch { "SQRT" => Math.Sqrt(x), "EXP" => Math.Exp(x), "LN" => Math.Log(x), _ => Math.Log10(x) });
        }
        int scale = Name == "ROUND" && args.Count == 2 ? checked((int)args.GetInt64(1)) : 0;
        if (first is float or double || Name == "POWER" && args[1].Type is not (DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64)
            || Name == "MOD" && args[1].Type is DatabaseType.Float32 or DatabaseType.Float64)
        {
            double result = Name switch
            {
                "ROUND" => RoundApproximate(x, scale), "FLOOR" => Math.Floor(x),
                "CEIL" or "CEILING" => Math.Ceiling(x), "POWER" => Math.Pow(x, y), "MOD" => x % y,
                _ => throw new InvalidOperationException(),
            };
            result = Finite(result);
            if (first is float && Name != "MOD" && !(Name == "POWER" && args[1].Type is DatabaseType.Decimal or DatabaseType.Float32 or DatabaseType.Float64))
            {
                float single = (float)result;
                if (!float.IsFinite(single)) { throw SqlEvaluationException.NumericValueOutOfRange(Name + " overflowed REAL."); }
                return single;
            }
            return result;
        }
        decimal exact = Convert.ToDecimal(first, CultureInfo.InvariantCulture);
        decimal resultExact = Name switch
        {
            "ROUND" => RoundExact(exact, scale), "FLOOR" => decimal.Floor(exact),
            "CEIL" or "CEILING" => decimal.Ceiling(exact),
            "MOD" => exact % Convert.ToDecimal(args[1].ToObject(), CultureInfo.InvariantCulture),
            "POWER" => PowerExact(exact, Convert.ToInt64(args[1].ToObject(), CultureInfo.InvariantCulture)),
            _ => throw new InvalidOperationException(),
        };
        if (first is decimal || Name == "POWER" || Name == "MOD" && args[1].Type == DatabaseType.Decimal) { return resultExact; }
        return checked((long)resultExact);
    }

    private static decimal PowerExact(decimal value, long exponent)
    {
        bool inverse = exponent < 0;
        ulong remaining = inverse ? (ulong)(-(exponent + 1)) + 1 : (ulong)exponent;
        if (inverse) { value = 1 / value; }
        decimal result = 1;
        while (remaining != 0)
        {
            if ((remaining & 1) != 0) { result *= value; }
            remaining >>= 1;
            if (remaining != 0) { value *= value; }
        }
        return result;
    }

    private static decimal RoundExact(decimal value, int scale)
    {
        if (scale >= 28) { return value; }
        if (scale >= 0) { return decimal.Round(value, scale, MidpointRounding.AwayFromZero); }
        if (scale < -28)
        {
            if (scale == -29 && decimal.Abs(value) >= 50000000000000000000000000000m)
            { throw SqlEvaluationException.NumericValueOutOfRange("ROUND result exceeds DECIMAL."); }
            return 0m;
        }
        decimal factor = 1;
        for (int index = 0; index < -scale; index++) { factor *= 10; }
        return decimal.Round(value / factor, 0, MidpointRounding.AwayFromZero) * factor;
    }

    private static double RoundApproximate(double value, int scale)
        => scale >= 0 ? Math.Round(value, Math.Min(scale, 15), MidpointRounding.ToEven)
            : scale < -308 ? 0 : Math.Round(value / Math.Pow(10, -scale), MidpointRounding.ToEven) * Math.Pow(10, -scale);

    private static double Finite(double value) => double.IsFinite(value) ? value
        : throw SqlEvaluationException.NumericValueOutOfRange("Function result is not finite.");

    private static Rune[] Runes(string value) => value.EnumerateRunes().ToArray();

    private static string Join(ReadOnlySpan<Rune> runes)
    {
        var builder = new StringBuilder();
        foreach (var rune in runes) { builder.Append(rune.ToString()); }
        return builder.ToString();
    }

    private static long Position(string text, string pattern)
    {
        int index = Runes(text).AsSpan().IndexOf(Runes(pattern));
        return index < 0 ? 0 : (long)index + 1;
    }
}
