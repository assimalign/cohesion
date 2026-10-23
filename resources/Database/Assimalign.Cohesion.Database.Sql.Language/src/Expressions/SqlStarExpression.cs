namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents the <c>*</c> wildcard in a SELECT list or <c>COUNT(*)</c>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlStarExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlStarExpression.DebugView))]
public sealed class SqlStarExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlStarExpression"/>.
    /// </summary>
    /// <param name="location">The source location.</param>
    internal SqlStarExpression(Location? location)
        : base(location)
    {
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlStarExpression value)
        {
            _ = value;
        }
    }
}
