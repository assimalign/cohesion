using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Tests.TestObjects;

/// <summary>
/// The end of a test pipeline: records the exchange it receives and what that exchange reports while the
/// rest of the pipeline runs, which is when the rewrite's view and features are in place.
/// </summary>
internal sealed class DownstreamProbe
{
    public int Invocations { get; private set; }

    public IHttpContext? Context { get; private set; }

    public HttpPath? Path { get; private set; }

    public IHttpQueryCollection? Query { get; private set; }

    public HttpPath? EffectivePath { get; private set; }

    public HttpPath? PathBase { get; private set; }

    public IWebRewriteFeature? Rewrite { get; private set; }

    public Task InvokeAsync(IHttpContext context)
    {
        Invocations++;
        Context = context;
        Path = context.Request.Path;
        Query = context.Request.Query;
        EffectivePath = context.GetEffectivePath();
        PathBase = context.GetPathBase();
        Rewrite = context.Features.Get<IWebRewriteFeature>();
        return Task.CompletedTask;
    }
}
