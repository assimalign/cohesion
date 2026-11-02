using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.SecretStore.Hosting.ApplicationModel.Tests;

/// <summary>
/// The environment a developer's machine names: the SecretStore host serves plaintext HTTP on loopback only
/// in <c>Local</c>, and a gateway hands its own environment name to the programs it runs in process.
/// </summary>
internal static class LocalEnvironment
{
    /// <summary>
    /// Enters a scope whose environment name is <c>Local</c> until the returned value is disposed.
    /// </summary>
    public static IDisposable Enter()
    {
        return AppEnvironment.Enter(AppEnvironmentScope.Create(
            new Dictionary<string, string> { [AppEnvironment.Variables.Environment] = AppEnvironment.Keys.Local },
            CancellationToken.None));
    }
}
