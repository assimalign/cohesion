using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.MediaHub.Hosting.ApplicationModel.Tests;

/// <summary>
/// An action an application defines and adds to its resource: a stateful control whose content type,
/// <see cref="Greeting"/>, only the application's own <see cref="GreetingJsonContext"/> declares.
/// </summary>
internal sealed class GreetingAction : IApplicationInstanceAction
{
    private readonly ApplicationInstanceActionState<Greeting> _state;

    public GreetingAction(string text = "hello", string name = "greeting")
    {
        Name = name;
        _state = ApplicationInstanceActionState<Greeting>.Create(new Greeting(text), static _ => { });
    }

    public ActionName Name { get; }

    public ApplicationActionKind Kind => ApplicationActionKind.ReadWrite;

    public IApplicationInstanceActionState? State => _state;

    public async ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Operation == ApplicationActionOperation.Read)
        {
            return ApplicationActionResult.Success(_state.Value, context.Serializer);
        }

        Greeting? greeting = context.ReadInput<Greeting>();
        if (greeting is null || string.IsNullOrWhiteSpace(greeting.Text))
        {
            return ApplicationActionResult.Rejected("A greeting is required.");
        }

        await _state.SetAsync(greeting, cancellationToken);
        return ApplicationActionResult.Success();
    }
}
