using System;

namespace Assimalign.Cohesion.Connections.NamedPipes.Tests;

/// <summary>
/// Produces a process-unique pipe name so concurrently executing tests do not collide on one name.
/// </summary>
internal static class NamedPipeTestName
{
    public static string Create() => $"cohesion-test-{Guid.NewGuid():N}";
}
