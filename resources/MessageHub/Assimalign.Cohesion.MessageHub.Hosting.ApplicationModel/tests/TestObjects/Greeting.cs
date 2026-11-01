namespace Assimalign.Cohesion.MessageHub.Hosting.ApplicationModel.Tests;

/// <summary>
/// The content of <see cref="GreetingAction"/>: on the wire, <c>{ "text": "hello" }</c>.
/// </summary>
internal sealed record Greeting(string Text);
