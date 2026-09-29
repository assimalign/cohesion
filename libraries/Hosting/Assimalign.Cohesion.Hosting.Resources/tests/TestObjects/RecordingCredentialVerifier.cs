using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

/// <summary>A credential verifier that records the invocation context it was created for.</summary>
internal sealed class RecordingCredentialVerifier : IResourceCredentialVerifier
{
    internal RecordingCredentialVerifier(ResourceContext context)
    {
        Context = context;
    }

    internal ResourceContext Context { get; }

    public ValueTask<ResourceCredentialVerification> VerifyAsync(
        ResourceCredentialPresentation presentation,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(default(ResourceCredentialVerification));
}
