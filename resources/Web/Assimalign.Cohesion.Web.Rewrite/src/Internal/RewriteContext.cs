using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// One request's rule evaluation: the URL as the rules have rewritten it so far, and the action the rule
/// that just ran took.
/// </summary>
/// <remarks>
/// One instance per request that reaches the rules, created by <see cref="RewriteMiddleware"/>. It records
/// actions; the middleware applies them once evaluation ends, so a rule that throws leaves the response
/// untouched.
/// </remarks>
internal sealed class RewriteContext : IRewriteContext
{
    private readonly IHttpContext _httpContext;
    private readonly HttpPath _pathBase;
    private readonly HttpPath _entryPath;
    private readonly IHttpQueryCollection _entryQuery;

    private HttpPath _path;
    private IHttpQueryCollection _query;
    private string? _serializedQuery;
    private string? _pathAndQuery;
    private RewriteOutcome _outcome;
    private string? _location;
    private HttpStatusCode _statusCode;

    public RewriteContext(IHttpContext httpContext, HttpPath pathBase, HttpPath path, IHttpQueryCollection query)
    {
        _httpContext = httpContext;
        _pathBase = pathBase;
        _entryPath = path;
        _entryQuery = query;
        _path = path;
        _query = query;
    }

    /// <inheritdoc />
    public IHttpContext HttpContext => _httpContext;

    /// <inheritdoc />
    public HttpPath PathBase => _pathBase;

    /// <inheritdoc />
    public HttpPath Path => _path;

    /// <inheritdoc />
    public IHttpQueryCollection Query => _query;

    /// <summary>
    /// Gets what the rule that just ran asked for.
    /// </summary>
    public RewriteOutcome Outcome => _outcome;

    /// <summary>
    /// Gets the <c>Location</c> of a <see cref="RewriteOutcome.Redirect"/>.
    /// </summary>
    public string? RedirectLocation => _location;

    /// <summary>
    /// Gets the status of a <see cref="RewriteOutcome.Redirect"/>.
    /// </summary>
    public HttpStatusCode RedirectStatusCode => _statusCode;

    /// <summary>
    /// Gets a value indicating whether the rules changed the URL the middleware received.
    /// </summary>
    public bool IsRewritten => !_path.Equals(_entryPath) || !ReferenceEquals(_query, _entryQuery);

    /// <summary>
    /// Gets the current query, re-encoded the way <see cref="RewriteMatchTarget.PathAndQuery"/> matches it and
    /// a redirect carries it. Computed once per query.
    /// </summary>
    public string SerializedQuery => _serializedQuery ??= RewriteUrl.SerializeQuery(_query);

    /// <summary>
    /// Gets the text a pattern matches for <paramref name="target"/>, and the length of its path.
    /// </summary>
    public string GetMatchInput(RewriteMatchTarget target, out int pathLength)
    {
        string path = _path.Value;
        pathLength = path.Length;

        if (target == RewriteMatchTarget.Path)
        {
            return path;
        }

        return _pathAndQuery ??= SerializedQuery.Length == 0 ? path : string.Concat(path, "?", SerializedQuery);
    }

    /// <summary>
    /// Clears the flow a previous rule asked for, before the next rule runs.
    /// </summary>
    public void BeginRule() => _outcome = RewriteOutcome.Continue;

    /// <inheritdoc />
    public void Rewrite(HttpPath path, IHttpQueryCollection? query = null, RewriteFlow flow = RewriteFlow.Continue)
    {
        ThrowIfEnded();

        if (path.Value is not { Length: > 0 } value || value[0] != '/')
        {
            throw new ArgumentException("A rewritten path must be an origin-form path that starts with '/'.", nameof(path));
        }

        _outcome = flow switch
        {
            RewriteFlow.Continue => RewriteOutcome.Continue,
            RewriteFlow.SkipRemainingRules => RewriteOutcome.SkipRemainingRules,
            RewriteFlow.Restart => RewriteOutcome.Restart,
            _ => throw new ArgumentOutOfRangeException(nameof(flow), flow, "The flow is not a defined RewriteFlow value."),
        };

        if (!_path.Equals(path))
        {
            _path = path;
            _pathAndQuery = null;
        }

        if (query is not null && !ReferenceEquals(query, _query))
        {
            _query = query;
            _serializedQuery = null;
            _pathAndQuery = null;
        }
    }

    /// <inheritdoc />
    public void Redirect(string location, HttpStatusCode statusCode)
    {
        ThrowIfEnded();
        RewriteUrl.ValidateLocation(location);
        RewriteStatusCodes.ValidateRedirect(statusCode, nameof(statusCode));

        _location = location;
        _statusCode = statusCode;
        _outcome = RewriteOutcome.Redirect;
    }

    /// <inheritdoc />
    public void SkipRemainingRules()
    {
        ThrowIfEnded();
        _outcome = RewriteOutcome.SkipRemainingRules;
    }

    /// <inheritdoc />
    public void EndResponse()
    {
        ThrowIfEnded();
        _outcome = RewriteOutcome.EndResponse;
    }

    /// <summary>
    /// Ends evaluation with a <c>400</c>: the rule's target expanded to a URL no request can carry.
    /// </summary>
    public void RejectTarget()
    {
        ThrowIfEnded();
        _outcome = RewriteOutcome.BadRequest;
    }

    private void ThrowIfEnded()
    {
        if (_outcome is RewriteOutcome.Redirect or RewriteOutcome.EndResponse or RewriteOutcome.BadRequest)
        {
            throw new InvalidOperationException("The rule already ended the exchange with a redirect or a response; it can take no further action.");
        }
    }
}
