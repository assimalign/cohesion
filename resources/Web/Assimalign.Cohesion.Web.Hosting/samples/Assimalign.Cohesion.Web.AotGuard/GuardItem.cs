using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.AotGuard;

/// <summary>
/// The guard's JSON model, serialized through the source-generated <see cref="GuardJsonContext"/>.
/// </summary>
/// <param name="Id">The item identifier.</param>
/// <param name="Name">The item name.</param>
internal sealed record GuardItem(int Id, string Name);

/// <summary>
/// The source-generated serializer context registered with the Web serialization registry, so no
/// JSON path in the guard relies on reflection.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuardItem))]
internal sealed partial class GuardJsonContext : JsonSerializerContext
{
}
