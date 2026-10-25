using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A null, Boolean, number, or string literal.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlLiteralExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlLiteralExpression.DebugView))]
public sealed class OqlLiteralExpression : OqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="OqlLiteralExpression"/> class.</summary>
    /// <param name="value">The literal value.</param>
    /// <param name="location">The source span.</param>
    public OqlLiteralExpression(object? value, Location? location = null) : base(location)
    {
        Value = value;
    }

    /// <summary>Gets the literal's BCL value; parsed numbers retain their digits in a System.Text.Json.JsonElement until engine validation.</summary>
    public object? Value { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlLiteralExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlLiteralExpression value)
        {
            _target = value;
        }

        public object? Value => _target.Value;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
