using System;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>A function evaluated over an ordered partition of the query's result.</summary>
[System.Diagnostics.DebuggerDisplay("SqlWindowExpression")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class SqlWindowExpression : SqlExpression
{
    internal SqlWindowExpression(SqlFunctionCallExpression function, string? name, SqlWindowSpecification? specification, Location? location, SqlWindowNullTreatment nullTreatment = SqlWindowNullTreatment.Default)
        : base(location)
    {
        Function = function;
        NullTreatment = nullTreatment;
        WindowName = name;
        Specification = specification;
        Depth = 1 + Math.Max(function.Depth, specification?.ExpressionDepth ?? 0);
    }

    /// <summary>Gets the explicit NULL treatment, or the default RESPECT NULLS behavior.</summary>
    public SqlWindowNullTreatment NullTreatment { get; }
    /// <summary>Gets the function and its arguments.</summary>
    public SqlFunctionCallExpression Function { get; }
    /// <summary>Gets the directly referenced name in OVER name.</summary>
    public string? WindowName { get; }
    /// <summary>Gets the parenthesized specification, including any inherited name.</summary>
    public SqlWindowSpecification? Specification { get; }

    private sealed class DebugView
    {
        private readonly SqlWindowExpression _value;
        public DebugView(SqlWindowExpression value) { _value = value; }
        public SqlFunctionCallExpression Function => _value.Function;
        public SqlWindowNullTreatment NullTreatment => _value.NullTreatment;
        public string? WindowName => _value.WindowName;
        public SqlWindowSpecification? Specification => _value.Specification;
        public string? Text => _value.Text;
        public Location? Location => _value.Location;
    }
}
