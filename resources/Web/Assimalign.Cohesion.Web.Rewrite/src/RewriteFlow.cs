namespace Assimalign.Cohesion.Web.Rewrite;

/// <summary>
/// What the rule engine does after a rule rewrites the request.
/// </summary>
/// <remarks>
/// <para>
/// Rules run in registration order, once each, in a <em>pass</em>. A rewrite changes the URL every later
/// rule sees, and by default evaluation continues with the next rule. A redirect always ends evaluation,
/// and the pipeline with it, so it takes no flow.
/// </para>
/// <para>
/// <see cref="Restart"/> is the only way a rule set can loop, so the number of passes is bounded by
/// <see cref="RewriteOptions.MaxPasses"/>: a request that needs more fails with an
/// <see cref="System.InvalidOperationException"/> instead of looping.
/// </para>
/// </remarks>
public enum RewriteFlow
{
    /// <summary>
    /// Evaluate the next rule against the rewritten URL. The default.
    /// </summary>
    Continue = 0,

    /// <summary>
    /// Evaluate no further rule. The pipeline continues with the URL as rewritten so far, the way
    /// ASP.NET Core's <c>SkipRemainingRules</c>, IIS's <c>stopProcessing</c> and mod_rewrite's <c>[L]</c> do.
    /// </summary>
    SkipRemainingRules = 1,

    /// <summary>
    /// Evaluate the rules again from the first one, against the rewritten URL, the way mod_rewrite's
    /// <c>[N]</c> and nginx's <c>last</c> do. Each restart starts a new pass, and the passes are bounded by
    /// <see cref="RewriteOptions.MaxPasses"/>.
    /// </summary>
    Restart = 2,
}
