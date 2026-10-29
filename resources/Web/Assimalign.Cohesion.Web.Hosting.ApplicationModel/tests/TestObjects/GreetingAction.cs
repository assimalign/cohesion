using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Tests;

/// <summary>
/// An action an application defines and adds to its resource: a stateful control whose content type,
/// <see cref="Greeting"/>, only the application's own <see cref="GreetingJsonContext"/> declares.
/// </summary>
internal sealed class GreetingAction : IResourceAction
{
    private readonly ResourceActionState<Greeting> _state;

    public GreetingAction(string text = "hello", string name = "greeting")
    {
        Name = name;
        _state = ResourceActionState<Greeting>.Create(new Greeting(text), static _ => { });
    }

    public ResourceActionName Name { get; }

    public ResourceActionKind Kind => ResourceActionKind.ReadWrite;

    public IResourceActionState? State => _state;

    public async ValueTask<ResourceActionResult> ExecuteAsync(ResourceActionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Operation == ResourceActionKind.Read)
        {
            return ResourceActionResult.Success(_state.Value, context.Serializer);
        }

        Greeting? greeting = context.ReadInput<Greeting>();
        if (greeting is null || string.IsNullOrWhiteSpace(greeting.Text))
        {
            return ResourceActionResult.Rejected("A greeting is required.");
        }

        await _state.SetAsync(greeting, cancellationToken);
        return ResourceActionResult.Success();
    }
}
