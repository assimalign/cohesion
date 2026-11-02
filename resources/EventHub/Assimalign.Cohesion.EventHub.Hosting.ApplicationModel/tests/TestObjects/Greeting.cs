namespace Assimalign.Cohesion.EventHub.Hosting.ApplicationModel.Tests;

/// <summary>
/// The content of <see cref="GreetingAction"/>: on the wire, <c>{ "text": "hello" }</c>.
/// </summary>
internal sealed record Greeting(string Text);
