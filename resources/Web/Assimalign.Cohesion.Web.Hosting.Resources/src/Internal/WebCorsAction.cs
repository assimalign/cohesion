using System;
using System.Text.Json;
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
        _state = ResourceActionState<WebCorsPolicy>.Create(policy, WebResourceJsonContext.Default.WebCorsPolicy, Apply);
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
            return ResourceActionResult.Success(_state.Value, WebResourceJsonContext.Default.WebCorsPolicy);
        }

        WebCorsPolicy? policy = context.ReadInput(WebResourceJsonContext.Default.WebCorsPolicy);
        if (policy is null)
        {
            return ResourceActionResult.Rejected(ErrorMessages.CorsPolicyRequired);
        }

        try
        {
            await _state.SetAsync(policy, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            // Apply refused the policy (see Apply): the writer's error, and nothing changed.
            return ResourceActionResult.Rejected(exception.Message);
        }

        return ResourceActionResult.Success();
    }

    // A policy CORS refuses is not a value of the state's type: a write reports it as rejected and a
    // restore as a snapshot that does not fit, and the policy in effect stays.
    private void Apply(WebCorsPolicy policy)
    {
        IWebApplicationMiddleware middleware;
        try
        {
            middleware = CorsMiddlewareCapture.Create(policy);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new JsonException(exception.Message, exception);
        }

        Middleware.Replace(middleware);
    }
}
