using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>CAST</c> over a bound operand, with what binding resolved once: the target type (a built-in
/// type with its length, precision and scale, or a domain of the engine's type catalog) and the
/// casts of the engine's registry it may apply, one per storage type of the value it converts
/// (phase E3). JSON/JSONB retain the declared SQL identity despite their shared CLR storage.
/// The evaluator picks the cast by that identity or the value's storage type, applies the target's
/// length, precision and scale, and a domain target's check (<see cref="SqlCastConverter.Convert"/>).
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundCast")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundCast.DebugView))]
internal sealed class SqlBoundCast : SqlBoundExpression
{
    /// <summary>Initializes a bound CAST.</summary>
    /// <param name="operand">The bound operand.</param>
    /// <param name="targetText">The target as written, for messages.</param>
    /// <param name="target">The resolved target type.</param>
    /// <param name="casts">The casts the CAST may apply, by the storage type of the value it converts.</param>
    /// <param name="database">The database whose statement makes the CAST, for a domain check's context.</param>
    /// <param name="collation">The collation a domain check's input compares under.</param>
    /// <param name="location">The source expression span, if available.</param>
    /// <param name="sourceType">The source SQL identity when known, retaining JSON and JSONB.</param>
    /// <param name="assignment">Whether target modifiers enforce assignment overflow rules.</param>
    internal SqlBoundCast(SqlBoundExpression operand, string targetText, SqlType target, SqlCast?[] casts, DatabaseName database,
        Collation collation, Location? location = null, DatabaseType sourceType = DatabaseType.Null, bool assignment = false)
        : base(SqlBoundExpressionKind.Cast)
    {
        SourceType = sourceType;
        Assignment = assignment;
        Location = location;
        Operand = operand;
        TargetText = targetText;
        Target = target;
        Casts = casts;
        Database = database;
        Collation = collation;
        CheckTarget = target.Check is { } check && SqlFunctionResolver.CoercionTargets(check, 1) is { } targets
            ? targets[0]
            : DatabaseType.Null;
    }

    /// <summary>Gets the source identity, retaining JSON and JSONB despite their shared CLR representation.</summary>
    internal DatabaseType SourceType { get; }

    // Assignment coercion retains overflow checks for character modifiers.
    internal bool Assignment { get; }

    /// <summary>Gets the bound operand.</summary>
    internal SqlBoundExpression Operand { get; }

    /// <summary>Gets the target as written, with its type arguments.</summary>
    internal string TargetText { get; }

    /// <summary>Gets the resolved target type.</summary>
    internal SqlType Target { get; }

    /// <summary>
    /// Gets the cast the CAST applies to a value of each storage type, indexed by its
    /// <see cref="DatabaseType"/>; a null slot converts only a value of the target's own storage type.
    /// </summary>
    internal SqlCast?[] Casts { get; }

    /// <summary>Gets the database whose statement makes the CAST.</summary>
    internal DatabaseName Database { get; }

    /// <summary>Gets the collation a domain check's input compares under.</summary>
    internal Collation Collation { get; }

    /// <summary>Gets the storage type a domain check's argument converts to; <see cref="DatabaseType.Null"/> for none.</summary>
    internal DatabaseType CheckTarget { get; }

    /// <summary>Gets the source expression span.</summary>
    internal Location? Location { get; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundCast value)
        {
            _ = value;
        }
    }
}
