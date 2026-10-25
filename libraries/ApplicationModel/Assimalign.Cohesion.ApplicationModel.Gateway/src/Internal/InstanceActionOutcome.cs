using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>One instance's outcome for an action invocation.</summary>
/// <param name="Address">The instance's control-plane address, or null for an in-process instance.</param>
/// <param name="Succeeded">Whether the instance ran the action.</param>
/// <param name="Status">The HTTP status the instance answered with, if it answered.</param>
/// <param name="Result">The action's JSON result, when it succeeded.</param>
/// <param name="Error">Why the action did not run, when it did not.</param>
internal sealed record InstanceActionOutcome(string? Address, bool Succeeded, int? Status, JsonElement? Result, string? Error);
