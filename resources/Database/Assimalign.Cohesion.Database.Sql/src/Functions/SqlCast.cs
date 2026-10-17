using System;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// A conversion from one SQL type to another that <c>CAST</c> applies: an entry of the engine's
/// cast registry (<see cref="SqlTypeCollection.AddCast"/>), PostgreSQL's <c>pg_cast</c> row with its
/// <c>castfunc</c> and <c>castcontext</c> (<c>src/include/catalog/pg_cast.h</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>One registry for the engine and the application</b> (phase E3, owner decision 68 of
/// 2026-10-09). The engine's own conversions — text to a number, a number to text, an integer to
/// a narrower one, text to a date — are entries of this type and
/// registered through the same <see cref="SqlTypeCollection.AddCast"/> an application calls, so
/// <c>CAST</c> resolves and runs an application's cast exactly as a built-in one. An application
/// adds casts to or from its domains, and between two built-in types the standard library does not
/// convert; a pair the standard library converts already cannot be registered again, by anyone.
/// </para>
/// <para>
/// <b>What a cast does and what the engine does.</b> The conversion maps a value of the source's
/// storage type to the target's; NULL converts to NULL without a call. The engine then applies the
/// target's length, precision and scale (PostgreSQL's separate length coercion), and a domain
/// target's check, so a conversion never has to. An explicit character cast truncates to its
/// declared character count; NUMERIC rounds to its scale, with midpoint ties away from zero,
/// before checking precision. A cast whose source or target is a domain is
/// preferred to the cast of the domain's base type, which applies otherwise; between two types of
/// the same storage type no cast is needed (PostgreSQL's binary-coercible pair), unless one is
/// registered. A value of a domain cast to that same domain is never converted, as PostgreSQL
/// relabels it (<c>src/backend/parser/parse_coerce.c</c>): no cast applies, the domain's own casts
/// included, and the length, precision, scale and check do. A cast from a domain applies only where
/// the plan knows the operand's domain: a column of the domain, or a <c>CAST</c> to it, under any
/// <c>COLLATE</c>; any other expression over such a value (<c>COALESCE</c>, <c>CASE</c>, arithmetic)
/// has the base type, and its cast is the base type's.
/// </para>
/// <para>
/// <b>Failures.</b> A <see cref="FormatException"/>, <see cref="OverflowException"/> or
/// <see cref="InvalidCastException"/> the conversion throws is the value's fault: the statement fails
/// as a conversion failure naming the source and the target (<c>CAST from TEXT to DATE failed:
/// ...</c>), the original as its inner exception, as the built-in casts fail. Anything else it throws,
/// other than a <see cref="DatabaseException"/> (which passes unchanged), an exhausted stack or
/// memory, is a fault of the conversion's code: <c>COHSQLE007</c> (<c>Cast TEXT AS cents failed:
/// ...</c>), which leaves the session and an explicit transaction usable, as for a function. That
/// includes an <see cref="OperationCanceledException"/> unless the statement's own token is
/// canceled, regardless of the token carried by the exception. The conversion never receives the
/// statement's token; domain checks follow the same rule through <see cref="SqlScalarFunction"/>.
/// One instance serves every
/// session, so the conversion must be thread-safe.
/// </para>
/// </remarks>
public sealed class SqlCast
{
    private readonly Func<object, object?> _convert;

    internal SqlCast(SqlType source, SqlType target, SqlCastContext context, Func<object, object?> convert)
    {
        Source = source;
        Target = target;
        Context = context;
        _convert = convert;
    }

    /// <summary>Gets the type the cast converts from.</summary>
    public SqlType Source { get; }

    /// <summary>Gets the type the cast converts to.</summary>
    public SqlType Target { get; }

    /// <summary>Gets where the cast may be applied without being written.</summary>
    public SqlCastContext Context { get; }

    /// <summary>
    /// Creates a cast from a delegate. The CLR types map to SQL types when this method runs, as the
    /// typed function shorthands map them (<see cref="string"/> to <see cref="SqlType.Text"/>,
    /// <see cref="long"/> to <see cref="SqlType.BigInt"/>, <see cref="DateOnly"/> to
    /// <see cref="SqlType.Date"/>, and so on), and each must be the storage type of its SQL type, a
    /// domain's being its base type's.
    /// </summary>
    /// <typeparam name="TSource">The CLR type of the source's storage type.</typeparam>
    /// <typeparam name="TTarget">The CLR type of the target's storage type.</typeparam>
    /// <param name="source">The type the cast converts from.</param>
    /// <param name="target">The type the cast converts to.</param>
    /// <param name="convert">
    /// The conversion; never called with NULL. It must be thread-safe. A <see langword="null"/>
    /// result is SQL NULL.
    /// </param>
    /// <param name="context">Where the cast may be applied without being written.</param>
    /// <returns>The cast, to register with <see cref="SqlTypeCollection.AddCast"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/>, <paramref name="target"/> or <paramref name="convert"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// A type is a pseudo-type; the source and the target are the same type; or a CLR type is not
    /// the storage type of its SQL type.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="context"/> is not defined.</exception>
    /// <exception cref="NotSupportedException">A type argument has no SQL type.</exception>
    /// <example>
    /// <code>
    /// sql.Types.AddCast(SqlCast.Create&lt;string, string&gt;(SqlType.Text, EmailType.Domain,
    ///     static text =&gt; text.Trim().ToLowerInvariant(), SqlCastContext.Explicit));
    /// </code>
    /// </example>
    public static SqlCast Create<TSource, TTarget>(SqlType source, SqlType target, Func<TSource, TTarget> convert, SqlCastContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(convert);
        if (!Enum.IsDefined(context))
        {
            throw new ArgumentOutOfRangeException(nameof(context), context, "The cast context is not defined.");
        }
        if (source.IsPseudo || target.IsPseudo)
        {
            throw new ArgumentException(
                $"A cast converts between storage types and domains; {(source.IsPseudo ? source : target).Name} is a pseudo-type.",
                source.IsPseudo ? nameof(source) : nameof(target));
        }
        if (source.Equals(target) || (!source.IsDomain && !target.IsDomain && source.Storage.Type == target.Storage.Type))
        {
            throw new ArgumentException($"A cast from {source.Name} to {target.Name} converts a type to itself.", nameof(target));
        }

        RequireStorage(SqlValueConverter<TSource>.Type, source, "TSource", nameof(source));
        RequireStorage(SqlValueConverter<TTarget>.Type, target, "TTarget", nameof(target));
        return new SqlCast(source, target, context,
            value => SqlValueConverter<TTarget>.ToValue(convert(SqlValueConverter<TSource>.FromValue(SqlValue.FromObject(value)))).ToObject());
    }

    /// <summary>Returns the cast as a diagnostic names it: <c>TEXT AS DATE</c>.</summary>
    /// <returns>The description.</returns>
    public override string ToString() => $"{Source.Name} AS {Target.Name}";

    /// <summary>
    /// Converts a value of the source's storage type, in the row's representation, to the target's;
    /// the evaluator calls it for a value that is not NULL and codes what it throws.
    /// </summary>
    /// <param name="value">The value, not NULL.</param>
    /// <returns>The converted value in the row's representation; null for SQL NULL.</returns>
    internal object? Apply(object value) => _convert(value);

    private static void RequireStorage(SqlType mapped, SqlType type, string parameter, string argument)
    {
        if (mapped.Storage.Type != type.Storage.Type &&
            !(mapped.Storage.Type == DatabaseType.String && type.Storage.Type == DatabaseType.Character))
        {
            throw new ArgumentException(
                $"{parameter} maps to {mapped.Name}, but {type.Name}{(type.IsDomain ? $" (a domain over {type.BaseType!.Name})" : string.Empty)} " +
                $"is stored as {SqlType.NameOf(type.Storage.Type)}; give the cast the CLR type of its storage.", argument);
        }
    }
}
