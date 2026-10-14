namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// What the rule that just ran asked the engine to do.
/// </summary>
internal enum RewriteOutcome
{
    /// <summary>Run the next rule.</summary>
    Continue = 0,

    /// <summary>Run no further rule; continue the pipeline.</summary>
    SkipRemainingRules,

    /// <summary>Run the rules again from the first one.</summary>
    Restart,

    /// <summary>Answer with the redirect the rule set and end the exchange.</summary>
    Redirect,

    /// <summary>The rule answered the exchange; end it.</summary>
    EndResponse,

    /// <summary>
    /// A rule's target expanded to a URL no request can carry (it decodes to a space, <c>?</c>, <c>#</c>, a
    /// control character or NUL): answer <c>400</c>, as a transport answers such a request target.
    /// </summary>
    BadRequest,
}
