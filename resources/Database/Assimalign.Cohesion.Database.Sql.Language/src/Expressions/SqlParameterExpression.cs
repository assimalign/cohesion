namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parameter reference such as <c>@param</c> or <c>$name</c>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlParameterExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlParameterExpression.DebugView))]
public sealed class SqlParameterExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlParameterExpression"/>.
    /// </summary>
    /// <param name="parameterName">The parameter name including its prefix.</param>
    /// <param name="location">The source location.</param>
    internal SqlParameterExpression(string parameterName, Location? location)
        : base(location)
    {
        ParameterName = parameterName;
    }

    /// <summary>
    /// Gets the parameter name (e.g., <c>@param</c> or <c>$name</c>).
    /// </summary>
    public string ParameterName { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Language.SqlParameterExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlParameterExpression value)
        {
            _target = value;
        }

        public string ParameterName => _target.ParameterName;
    }
}
