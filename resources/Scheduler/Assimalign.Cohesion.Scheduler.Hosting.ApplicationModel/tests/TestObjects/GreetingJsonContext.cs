using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Scheduler.Hosting.ApplicationModel.Tests;

// The application's own context for the content of the action it adds to its resource.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Greeting))]
internal sealed partial class GreetingJsonContext : JsonSerializerContext
{
}
