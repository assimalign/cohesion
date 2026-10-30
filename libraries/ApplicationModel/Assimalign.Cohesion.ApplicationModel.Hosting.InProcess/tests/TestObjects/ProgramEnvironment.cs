namespace Assimalign.Cohesion.ApplicationModel.Hosting.InProcess.Tests;

/// <summary>What an action read from the environment it ran in.</summary>
/// <param name="Resource">The resource name the environment holds, if any.</param>
public sealed record ProgramEnvironment(string? Resource);
