using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Web.Cors;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Internal;

/// <summary>
/// The <c>cors</c> action over the application's <c>cors</c> setting, which <c>AddCors</c> registered:
/// a read reports the default policy in effect, and a write or a restore replaces it.
/// </summary>
/// <remarks>
/// The action keeps no copy: its state is the setting, read and written through. The middleware
/// <c>UseCors()</c> placed reads the setting on each request, so a change applies from the next one.
/// </remarks>
internal sealed class WebCorsAction : IResourceAction, IResourceActionState
{
    private readonly IWebApplicationSetting<CorsPolicy?> _setting;

    public WebCorsAction(IWebApplicationSetting<CorsPolicy?> setting)
    {
        _setting = setting;
        Name = setting.Name;
    }

    public ResourceActionName Name { get; }

    public ResourceActionKind Kind => ResourceActionKind.ReadWrite;

    public IResourceActionState? State => this;

    /// <summary>
    /// Gets the default policy in effect.
    /// </summary>
    public WebCorsPolicy Current => WebCorsPolicyMapping.FromPolicy(_setting.Value);

    public ValueTask<ResourceActionResult> ExecuteAsync(ResourceActionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Operation == ResourceActionKind.Read)
        {
            return ValueTask.FromResult(ResourceActionResult.Success(Current, context.Serializer));
        }

        WebCorsPolicy? policy = context.ReadInput<WebCorsPolicy>();
        if (policy is null)
        {
            return ValueTask.FromResult(ResourceActionResult.Rejected(ErrorMessages.CorsPolicyRequired));
        }

        Apply(policy);
        return ValueTask.FromResult(ResourceActionResult.Success());
    }

    public ReadOnlyMemory<byte> GetSnapshot(IResourceContentSerializer serializer)
    {
        return serializer.Serialize(Current);
    }

    public ValueTask RestoreAsync(ReadOnlyMemory<byte> snapshot, IResourceContentSerializer serializer, CancellationToken cancellationToken = default)
    {
        WebCorsPolicy policy = serializer.Deserialize<WebCorsPolicy>(snapshot)
            ?? throw new ResourceContentException(ErrorMessages.CorsPolicyRequired);

        Apply(policy);
        return ValueTask.CompletedTask;
    }

    // A policy CORS refuses is content the instance does not accept: a write reports it as rejected,
    // a restore as a snapshot that does not fit, and the policy in effect stays.
    private void Apply(WebCorsPolicy policy)
    {
        CorsPolicy? built;
        try
        {
            built = WebCorsPolicyMapping.ToPolicy(policy);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new ResourceContentException(exception.Message, exception);
        }

        _setting.Set(built);
    }
}
