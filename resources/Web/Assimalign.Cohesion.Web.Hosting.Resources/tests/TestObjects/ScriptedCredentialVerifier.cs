using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

/// <summary>A registered credential verifier that records presentations and returns a scripted result.</summary>
internal sealed class ScriptedCredentialVerifier : IResourceCredentialVerifier
{
    internal ScriptedCredentialVerifier(ResourceCredentialVerification result)
    {
        Result = result;
    }

    internal ResourceCredentialVerification Result { get; }

    internal List<ResourceCredentialPresentation> Presentations { get; } = [];

    public ValueTask<ResourceCredentialVerification> VerifyAsync(
        ResourceCredentialPresentation presentation,
        CancellationToken cancellationToken = default)
    {
        Presentations.Add(presentation);
        return ValueTask.FromResult(Result);
    }

    /// <summary>
    /// Registers the verifier under a fresh resource-assembly identity and binds that identity to the
    /// context, as an area builder does when it resolves its control plane.
    /// </summary>
    /// <param name="context">The invocation context the middleware authorizes against.</param>
    /// <param name="name">A unique assembly name; runtime registrations are process-wide.</param>
    internal void BindTo(ResourceContext context, string name)
    {
        var assembly = new RegistrationAssembly(name);
        ResourceRuntime.RegisterCredentialVerifier(assembly, _ => this);
        using (ResourceRuntime.CreateScope(context))
        {
            ResourceRuntime.TryCreateControlPlane(assembly, out _);
        }
    }

    private sealed class RegistrationAssembly : Assembly
    {
        private readonly string _name;

        internal RegistrationAssembly(string name)
        {
            _name = name;
        }

        public override AssemblyName GetName() => new(_name);

        public override AssemblyName GetName(bool copiedName) => GetName();
    }
}
