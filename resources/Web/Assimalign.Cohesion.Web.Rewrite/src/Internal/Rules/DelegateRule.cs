using System;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// A rule written as a delegate over the public <see cref="IRewriteContext"/>.
/// </summary>
internal sealed class DelegateRule : IRewriteRule
{
    private readonly Action<IRewriteContext> _rule;

    public DelegateRule(Action<IRewriteContext> rule)
    {
        _rule = rule;
    }

    /// <inheritdoc />
    public void Apply(IRewriteContext context) => _rule(context);
}
