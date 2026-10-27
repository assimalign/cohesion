using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A searched or simple CASE expression, evaluated in branch order.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlCaseExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlCaseExpression.DebugView))]
public sealed class OqlCaseExpression : OqlExpression
{
    /// <summary>Initializes a CASE expression.</summary>
    /// <param name="operand">The simple CASE operand, or null for searched CASE.</param>
    /// <param name="branches">Ordered WHEN keys and THEN values.</param>
    /// <param name="otherwise">The ELSE expression, or null for an implicit NULL result.</param>
    /// <param name="location">The source span.</param>
    public OqlCaseExpression(OqlExpression? operand, IReadOnlyList<KeyValuePair<OqlExpression, OqlExpression>> branches, OqlExpression? otherwise = null, Location? location = null) : base(location)
    {
        Operand = operand;
        Branches = branches;
        Otherwise = otherwise;
    }

    /// <summary>Gets the simple CASE operand, or null for searched CASE.</summary>
    public OqlExpression? Operand { get; }
    /// <summary>Gets the ordered WHEN keys and THEN values.</summary>
    public IReadOnlyList<KeyValuePair<OqlExpression, OqlExpression>> Branches { get; }
    /// <summary>Gets the ELSE expression, or null for implicit NULL.</summary>
    public OqlExpression? Otherwise { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlCaseExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlCaseExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression? Operand => _target.Operand;
        public global::System.Collections.Generic.IReadOnlyList<global::System.Collections.Generic.KeyValuePair<global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression, global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression>> Branches => _target.Branches;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression? Otherwise => _target.Otherwise;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
