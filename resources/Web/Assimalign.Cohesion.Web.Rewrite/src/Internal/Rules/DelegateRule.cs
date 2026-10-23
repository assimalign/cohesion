using System;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// A rule written as a delegate over the public <see cref="IRewriteContext"/>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("DelegateRule")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class DelegateRule : IRewriteRule
{
    private readonly Action<IRewriteContext> _rule;

    public DelegateRule(Action<IRewriteContext> rule)
    {
        _rule = rule;
    }

    /// <inheritdoc />
    public void Apply(IRewriteContext context) => _rule(context);

    private sealed class DebugView
    {
        public DebugView(DelegateRule value)
        {
        }
    }
}
