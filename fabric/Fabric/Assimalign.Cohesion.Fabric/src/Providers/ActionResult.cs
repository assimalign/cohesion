using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The outcome of a resource action.
/// </summary>
/// <param name="Succeeded">Whether the action completed.</param>
/// <param name="Outputs">What the action returns to its caller.</param>
/// <param name="Faults">The causes, when <paramref name="Succeeded"/> is <see langword="false"/>.</param>
public sealed record ActionResult(bool Succeeded, IReadOnlyDictionary<string, JsonElement> Outputs, IReadOnlyList<FabricFault> Faults);
