namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A statement parameter bound to the value the statement supplied for it. A statement's parameter
/// values are fixed before it is planned, so the name is looked up once, not per row; a parameter
/// with no supplied value binds as a <see cref="SqlBoundFailure"/> that reports it when evaluated.
/// </summary>
internal sealed class SqlBoundParameter : SqlBoundExpression
{
    /// <summary>Initializes a bound parameter.</summary>
    /// <param name="name">The parameter's name as written, sigil included.</param>
    /// <param name="value">The supplied value, which may be null.</param>
    internal SqlBoundParameter(string name, object? value)
        : base(SqlBoundExpressionKind.Parameter)
    {
        Name = name;
        Value = value;
    }

    /// <summary>Gets the parameter's name as written, sigil included.</summary>
    internal string Name { get; }

    /// <summary>Gets the supplied value.</summary>
    internal object? Value { get; }
}
