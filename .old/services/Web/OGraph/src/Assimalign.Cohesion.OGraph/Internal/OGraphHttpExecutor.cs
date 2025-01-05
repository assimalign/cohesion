using System;
using Assimalign.OGraph;
using Assimalign.OGraph.Server;
using Assimalign.Cohesion.Net.Http;
using System.Threading.Tasks;
using System.Threading;

namespace Assimalign.Cohesion.OGraph.Internal;

internal class OGraphHttpExecutor : IHttpContextExecutor
{
    public readonly IOGraphExecutor executor;

    public OGraphHttpExecutor(IOGraphExecutor executor)
    {
        this.executor = executor;
    }
    public OGraphExecutorContext Content { get; } = new();

    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}