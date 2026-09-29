using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

using Assimalign.Cohesion.Http;

/// <summary>
/// Forwards to a pipeline passed to <c>IWebApplicationBuilder.AddPipeline</c>.
/// </summary>
/// <remarks>
/// The hosted pipeline is a factory registration, and the service provider disposes what its
/// factories return. Wrapping the caller's pipeline keeps it borrowed, like every other instance
/// handed to the builder.
/// </remarks>
internal sealed class BorrowedWebApplicationPipeline : IWebApplicationPipeline
{
    private readonly IWebApplicationPipeline _pipeline;

    internal BorrowedWebApplicationPipeline(IWebApplicationPipeline pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        return _pipeline.ExecuteAsync(context, cancellationToken);
    }
}
