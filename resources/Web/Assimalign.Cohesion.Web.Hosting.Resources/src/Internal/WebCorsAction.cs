using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Internal;

/// <summary>
/// The <c>cors</c> action: reads the default CORS policy in effect and, on a write, replaces it.
/// The policy is the action's state, so the gateway restores the last written policy on every
/// instance that starts before the resource is next deployed.
/// </summary>
internal sealed class WebCorsAction : IResourceAction
{
    private readonly ResourceActionState<WebCorsPolicy> _state;

    public WebCorsAction(WebCorsPolicy policy)
    {
        Middleware = new ResourceCorsMiddleware(CorsMiddlewareCapture.Create(policy));
        _state = ResourceActionState<WebCorsPolicy>.Create(policy, Apply);
    }

    public ResourceCorsMiddleware Middleware { get; }

    public WebCorsPolicy Current => _state.Value;

    public ResourceActionName Name => "cors";

    public ResourceActionKind Kind => ResourceActionKind.ReadWrite;

    public IResourceActionState? State => _state;

    public async ValueTask<ResourceActionResult> ExecuteAsync(ResourceActionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Operation == ResourceActionKind.Read)
        {
            return ResourceActionResult.Success(_state.Value, context.Serializer);
        }

        WebCorsPolicy? policy = context.ReadInput<WebCorsPolicy>();
        if (policy is null)
        {
            return ResourceActionResult.Rejected(ErrorMessages.CorsPolicyRequired);
        }

        // A policy CORS refuses throws from Apply; the control plane reports it as rejected.
        await _state.SetAsync(policy, cancellationToken).ConfigureAwait(false);
        return ResourceActionResult.Success();
    }

    // A policy CORS refuses is content the instance does not accept: a write reports it as rejected,
    // a restore as a snapshot that does not fit, and the policy in effect stays.
    private void Apply(WebCorsPolicy policy)
    {
        IWebApplicationMiddleware middleware;
        try
        {
            middleware = CorsMiddlewareCapture.Create(policy);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new ResourceContentException(exception.Message, exception);
        }

        Middleware.Replace(middleware);
    }
}
