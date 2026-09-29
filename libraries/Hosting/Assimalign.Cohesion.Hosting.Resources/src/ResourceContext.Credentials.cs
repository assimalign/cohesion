using System;
using System.Reflection;
using System.Threading;

namespace Assimalign.Cohesion.Hosting.Resources;

public sealed partial class ResourceContext
{
    private readonly object _credentialVerifierGate = new();
    private Assembly? _resourceAssembly;
    private IResourceCredentialVerifier? _credentialVerifier;

    /// <summary>
    /// Gets the resource executable assembly whose registrations this invocation resolved, or null before
    /// an area builder resolved its control plane.
    /// </summary>
    internal Assembly? ResourceAssembly => Volatile.Read(ref _resourceAssembly);

    /// <summary>Records the invocation's resource assembly; the first recorded assembly wins.</summary>
    /// <param name="assembly">The logical resource executable assembly.</param>
    internal void SetResourceAssembly(Assembly assembly)
    {
        _ = Interlocked.CompareExchange(ref _resourceAssembly, assembly, null);
    }

    /// <summary>Returns this invocation's credential verifier, creating it once from a factory.</summary>
    /// <param name="factory">The registered verifier factory.</param>
    /// <returns>The invocation's credential verifier.</returns>
    /// <exception cref="InvalidOperationException">The factory returned null.</exception>
    internal IResourceCredentialVerifier GetOrCreateCredentialVerifier(
        Func<ResourceContext, IResourceCredentialVerifier> factory)
    {
        IResourceCredentialVerifier? verifier = Volatile.Read(ref _credentialVerifier);
        if (verifier is not null)
        {
            return verifier;
        }

        lock (_credentialVerifierGate)
        {
            verifier = _credentialVerifier;
            if (verifier is null)
            {
                verifier = factory.Invoke(this)
                    ?? throw new InvalidOperationException(
                        $"Resource assembly '{ResourceAssembly?.GetName().Name}' returned a null credential verifier.");
                Volatile.Write(ref _credentialVerifier, verifier);
            }

            return verifier;
        }
    }
}
