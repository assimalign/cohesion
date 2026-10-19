using System;
using System.Globalization;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Runs a bound <c>CAST</c> (phase E3): the cast of the engine's registry chosen for the value's
/// storage type (<see cref="SqlStandardCasts"/> and the application's), or the value unchanged when
/// it has the target's storage type already; then the target's length, precision and scale; then a
/// domain target's check. Numeric modifiers round to scale; explicit character casts truncate.
/// </summary>
internal static class SqlCastConverter
{
    /// <summary>Converts a scalar, preserving null and rejecting a value no registered cast converts.</summary>
    /// <param name="value">The operand's value.</param>
    /// <param name="cast">The bound CAST.</param>
    /// <param name="cancellationToken">The statement's cancellation token, for a domain check's context.</param>
    /// <param name="copySharedBinary">Whether a converted binary result is being delivered into a row.</param>
    /// <returns>The converted value; null for NULL.</returns>
    /// <exception cref="DatabaseException">
    /// The conversion failed on the value; a <see cref="DatabaseException"/> a
    /// conversion threw passes unchanged.
    /// </exception>
    /// <exception cref="SqlEvaluationException">
    /// No cast converts the value's type (<c>COHSQLE011</c>), or the conversion's code failed
    /// (<c>COHSQLE007</c>).
    /// </exception>
    /// <exception cref="SqlConstraintViolationException">The value violates the domain target's check.</exception>
    internal static object? Convert(object? value, SqlBoundCast cast, CancellationToken cancellationToken, bool copySharedBinary = false)
    {
        object? converted = null;
        if (value is not null)
        {
            var source = cast.SourceType is DatabaseType.Json or DatabaseType.JsonBinary ? cast.SourceType : StorageTypeOf(value);
            SqlCast? registered = source == DatabaseType.Null ? null : cast.Casts[(int)source];
            if (registered is null && source != DatabaseType.Null && source != cast.Target.Storage.Type)
            {
                // The planner refuses this pair when it knows the operand's type; only a value
                // reveals a parameter's, so the same code is raised here.
                throw SqlEvaluationException.CannotCast(SqlType.NameOf(source), cast.Target);
            }

            try
            {
                if (registered is not null)
                {
                    // The built-in text conversion needs the destination scale before reducing
                    // its digits to Decimal. Application casts still own their conversions.
                    converted = (value is string || value is SqlCharacter) && cast.Target.Storage.Type == DatabaseType.Decimal
                        && SqlStandardCasts.Contains(registered)
                        ? ParseNumericText(value is SqlCharacter fixedText ? fixedText.Text : (string)value, cast.Target.Storage)
                        : registered.Apply(value);
                    if (copySharedBinary) { converted = SqlBinaryValue.CopyShared(converted); }
                }
                else if (source != DatabaseType.Null)
                {
                    converted = value;
                }
                else
                {
                    throw new InvalidCastException("Unsupported source/target pair.");
                }

                if (converted is not null)
                {
                    converted = ApplyModifiers(converted, cast.Target.Storage, isExplicit: true);
                }
            }
            catch (SqlEvaluationException exception)
            {
                throw exception.WithContext($"CAST from {SqlType.NameOf(source)} to {cast.TargetText} failed");
            }
            catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
            {
                // The value's fault, the built-ins' only failures: the conversion contract's message.
                throw SqlEvaluationException.CastValueFailed($"CAST from {SqlType.NameOf(source)} to {cast.TargetText} failed: {exception.Message}", exception);
            }
            catch (Exception exception) when (registered is not null && SqlEvaluationException.IsCastFailure(exception, cancellationToken))
            {
                // A fault of the conversion's code, an application's or a built-in's alike.
                throw SqlEvaluationException.CastFailed(registered, exception);
            }
        }

        // A domain's check sees every value the CAST produces; a strict check passes NULL uncalled.
        // Its refusal is the one a write raises for the domain (PostgreSQL's 23514 for both).
        if (cast.Target.Check is { } check && (converted is not null || check.NullBehavior == SqlNullBehavior.CalledOnNullInput)
            && check.InvokeResolved(converted, cast.CheckTarget, cast.Database, cast.Collation, cancellationToken) is false)
        {
            throw new SqlConstraintViolationException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.CastDomainViolation, cast.TargetText, cast.Target.Name, check),
                cast.Target.Name, string.Empty, "DOMAIN");
        }

        return converted;
    }

    /// <summary>
    /// Applies the declared bounds after storage conversion, for CAST, column assignments and
    /// DEFAULTs alike. Domains use the bounds of their base type before evaluating their check.
    /// </summary>
    /// <param name="value">The converted, non-null value of the target's storage type.</param>
    /// <param name="target">The target's storage type and constraints.</param>
    /// <param name="isExplicit">Whether an explicit CAST permits character truncation.</param>
    /// <returns>The bounded value, rounded or truncated where the coercion permits it.</returns>
    /// <exception cref="SqlEvaluationException">A character assignment exceeds its length (22001), or a rounded numeric exceeds its precision (22003).</exception>
    /// <exception cref="InvalidCastException">A binary value exceeds its declared length.</exception>
    internal static object ApplyModifiers(object value, DatabaseTypeInfo target, bool isExplicit = false)
    {
        if (value is SqlCharacter character) { value = character.Value; }
        if (target.Type == DatabaseType.Character)
        {
            string text = (string)value;
            // PostgreSQL varchar.c:284-286: an unconstrained bpchar typmod passes through.
            if (target.MaxLength is not int length) { return new SqlCharacter(text); }
            int characters = 0;
            int end = 0;
            // PostgreSQL varchar.c:130-190 and 271-339: count characters, pad on storage;
            // only an explicit cast may discard non-space overflow.
            foreach (var rune in text.EnumerateRunes())
            {
                if (characters == length)
                {
                    if (!isExplicit && text.AsSpan(end).ContainsAnyExcept(' '))
                    {
                        throw SqlEvaluationException.StringDataRightTruncation(length);
                    }
                    return new SqlCharacter(text[..end]);
                }
                characters++;
                end += rune.Utf16SequenceLength;
            }
            return new SqlCharacter(characters == length ? text : text + new string(' ', length - characters));
        }
        switch (value)
        {
            case string text when target.MaxLength is int length && text.Length > length:
                // PostgreSQL varchar.c:605-643: lengths count characters, not encoded bytes;
                // explicit CAST truncates, assignment only discards excess ASCII spaces.
                int characters = 0;
                int end = 0;
                foreach (var rune in text.EnumerateRunes())
                {
                    if (characters == length)
                    {
                        if (!isExplicit && text.AsSpan(end).ContainsAnyExcept(' '))
                        {
                            throw SqlEvaluationException.StringDataRightTruncation(length);
                        }
                        return text[..end];
                    }
                    characters++;
                    end += rune.Utf16SequenceLength;
                }
                return text;
            case byte[] bytes when target.MaxLength is int length && bytes.Length > length:
                throw new InvalidCastException($"Binary length exceeds {length}; truncation is not supported.");
            case decimal number when target.Type == DatabaseType.Decimal:
                int? scale = NumericScale(target);
                // PostgreSQL numeric.c:7693-7750 rounds before checking the integer digits;
                // round_var at numeric.c:11720-11748 rounds midpoint ties away from zero.
                // System.Decimal cannot represent scale beyond 28, so larger scales are a no-op.
                if (scale is >= 0 and < maxDecimalScale)
                {
                    number = decimal.Round(number, scale.Value, MidpointRounding.AwayFromZero);
                }
                if (target.Precision is int precision && precision - (scale ?? 0) is var digits && digits <= maxDecimalScale)
                {
                    decimal limit = 1m;
                    for (int i = 0; i < digits; i++) { limit *= 10m; }
                    if (number <= -limit || number >= limit)
                    {
                        throw SqlEvaluationException.NumericValueOutOfRange(
                            $"numeric field overflow: Value exceeds precision {precision} and scale {scale ?? 0}.");
                    }
                }
                return number;
            default:
                return value;
        }
    }

    // The largest scale System.Decimal holds, and the most integer digits whose power of ten it holds.
    private const int maxDecimalScale = 28;

    /// <summary>
    /// The storage type of a value as the row holds it: exactly one of the codec's CLR types, so a
    /// parameter of another CLR type (<see cref="byte"/>, <see cref="char"/>) has none and is refused,
    /// as it was before the cast registry.
    /// </summary>
    /// <param name="value">The value, not NULL.</param>
    /// <returns>The storage type; <see cref="DatabaseType.Null"/> for a CLR type the codec does not store.</returns>
    private static DatabaseType StorageTypeOf(object value) => value switch
    {
        SqlCharacter => DatabaseType.Character,
        string => DatabaseType.String,
        long => DatabaseType.Int64,
        int => DatabaseType.Int32,
        decimal => DatabaseType.Decimal,
        bool => DatabaseType.Boolean,
        short => DatabaseType.Int16,
        sbyte => DatabaseType.Int8,
        double => DatabaseType.Float64,
        float => DatabaseType.Float32,
        byte[] => DatabaseType.Binary,
        DateOnly => DatabaseType.Date,
        TimeOnly => DatabaseType.Time,
        DateTime => DatabaseType.DateTime,
        DateTimeOffset => DatabaseType.DateTimeOffset,
        TimeSpan => DatabaseType.TimeSpan,
        Guid => DatabaseType.Guid,
        _ => DatabaseType.Null,
    };

    /// <summary>
    /// Reads SQL numeric literals, including exponent notation, without rounding an
    /// oversized fractional mantissa before CAST can enforce its target constraints.
    /// </summary>
    internal static decimal ParseNumericLiteral(string text) => ParseExactDecimal(text, allowLiteralSyntax: true);

    /// <summary>
    /// Rounds numeric text directly to the destination scale before requiring an exact Decimal
    /// representation. CAST, assignments and DEFAULTs share this conversion and its range error.
    /// </summary>
    internal static decimal ParseNumericText(string text, DatabaseTypeInfo target, bool allowLiteralSyntax = false)
    {
        try
        {
            return ParseExactDecimal(text, allowLiteralSyntax, NumericScale(target));
        }
        catch (OverflowException exception)
        {
            throw SqlEvaluationException.NumericValueOutOfRange(exception.Message, exception);
        }
    }

    private static int? NumericScale(DatabaseTypeInfo target)
    {
        int? scale = target.Scale ?? (target.Precision is null ? null : 0);
        if (target.Precision is < 1 || scale is < 0 || scale > target.Precision)
        {
            throw new SqlPlanningException("COHSQLE018", "Invalid decimal precision or scale.");
        }
        return scale;
    }

    /// <summary>Parses fixed-point text exactly, optionally rounding its digits to a declared scale first.</summary>
    /// <param name="text">The original numeric text.</param>
    /// <param name="allowLiteralSyntax">Whether SQL numeric literal decimal-point and exponent forms are allowed.</param>
    /// <param name="targetScale">The declared scale, or null to require the original value to fit exactly.</param>
    /// <returns>The exact value after any declared-scale rounding.</returns>
    internal static decimal ParseExactDecimal(string text, bool allowLiteralSyntax = false, int? targetScale = null)
    {
        string source = text.Trim();
        bool negative = source.StartsWith('-');
        if (source.StartsWith('+') || negative) { source = source[1..]; }
        int exponent = 0;
        int exponentStart = source.IndexOfAny(['e', 'E']);
        if (allowLiteralSyntax && exponentStart >= 0)
        {
            exponent = int.Parse(source.AsSpan(exponentStart + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            source = source[..exponentStart];
        }
        int point = source.IndexOf('.');
        if (source.Length == 0 || source == "." ||
            (!allowLiteralSyntax && (point == 0 || point == source.Length - 1)))
        {
            throw new FormatException("Expected signed fixed-point numeric text.");
        }
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] is < '0' or > '9' && i != point)
            {
                throw new FormatException("Expected signed fixed-point numeric text without grouping or exponent.");
            }
        }

        long scale = (point < 0 ? 0L : source.Length - point - 1L) - exponent;
        string digits = point < 0 ? source : source.Remove(point, 1);
        digits = digits.TrimStart('0');
        if (digits.Length == 0) { return 0m; }

        if (targetScale is int roundedScale && scale > roundedScale)
        {
            // PostgreSQL numeric.c:11703-11748 rounds the full digit sequence, carrying
            // when the first discarded digit is at least five, independently of the sign.
            // Work in the input's digits: a large exponent must not allocate padding.
            long keep = digits.Length - (scale - roundedScale);
            if (keep < 0) { return 0m; }
            bool carry = digits[(int)keep] >= '5';
            if (keep == 0)
            {
                if (!carry) { return 0m; }
                digits = "1";
            }
            else
            {
                char[] rounded = digits[..(int)keep].ToCharArray();
                for (int i = rounded.Length - 1; carry && i >= 0; i--)
                {
                    carry = rounded[i] == '9';
                    rounded[i] = carry ? '0' : (char)(rounded[i] + 1);
                }
                digits = carry ? "1" + new string(rounded) : new string(rounded);
            }
            scale = roundedScale;
        }

        string significant = digits.TrimEnd('0');
        scale -= digits.Length - significant.Length;
        digits = significant;
        if (scale > 28 || digits.Length > 29 || digits.Length - scale > 29)
        {
            throw new OverflowException("Text cannot be represented exactly as System.Decimal.");
        }

        // Parse only the integer coefficient: this cannot silently round.
        decimal coefficient = decimal.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        while (scale < 0)
        {
            coefficient *= 10m;
            scale++;
        }
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(coefficient, bits);
        return new decimal(bits[0], bits[1], bits[2], negative, (byte)scale);
    }
}
